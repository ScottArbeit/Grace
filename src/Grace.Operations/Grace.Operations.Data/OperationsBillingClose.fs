namespace Grace.Operations.Data

open Grace.Types.Usage
open Microsoft.Data.SqlClient
open System
open System.Data
open System.Runtime.ExceptionServices
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Reports the durable outcome of one eligible billing close attempt.
[<RequireQualifiedAccess>]
type BillingCloseOutcome =
    | Closed of ledgerEntryCount: int
    | AlreadyFinal
    | NotEligible
    | Blocked of code: string

/// Carries one explicit signed append-only adjustment or reversal at the complete pricing grain.
type ManualBillingCorrection =
    {
        BillingPeriodId: Guid
        EntryKind: ChargeLedgerEntryKind
        PriorChargeLedgerEntryId: Guid option
        FactKind: int
        BillableUsageKindMappingId: Guid
        BillableUsageKind: int
        CustomerPricingAssignmentId: Guid
        PricingPlanId: Guid
        PricingRateId: Guid
        CurrencyCode: string
        UnitName: string
        UnitQuantity: int64
        UnitPriceMicros: int64
        EffectiveFromUtc: DateTime
        EffectiveToUtc: DateTime
        QuantityDelta: int64
        ChargeMicrosDelta: int64
    }

/// Builds retry-stable identities for complete manual correction commands.
[<RequireQualifiedAccess>]
module ManualBillingCorrectionIdentity =
    /// Includes every immutable ledger dimension so only an exact command retry deduplicates.
    let entryId (correction: ManualBillingCorrection) (correlationId: string) =
        BillingPeriodRules.deterministicId [ correction.BillingPeriodId.ToString("D")
                                             correlationId
                                             string (int correction.EntryKind)
                                             correction.PriorChargeLedgerEntryId
                                             |> Option.map (fun value -> value.ToString("D"))
                                             |> Option.defaultValue "none"
                                             string correction.FactKind
                                             correction.BillableUsageKindMappingId.ToString("D")
                                             string correction.BillableUsageKind
                                             correction.CustomerPricingAssignmentId.ToString("D")
                                             correction.PricingPlanId.ToString("D")
                                             correction.PricingRateId.ToString("D")
                                             correction.CurrencyCode
                                             correction.UnitName
                                             string correction.UnitQuantity
                                             string correction.UnitPriceMicros
                                             string correction.EffectiveFromUtc.Ticks
                                             string correction.EffectiveToUtc.Ticks
                                             string correction.QuantityDelta
                                             string correction.ChargeMicrosDelta ]

/// Builds stable active-failure identities without conflating malformed empty fact identifiers.
[<RequireQualifiedAccess>]
module BillingIngestionFailureIdentity =
    /// Uses the fact id when valid and otherwise the broker identity plus complete period scope.
    let failureId (fact: UsageFact) failureCode messageIdentity =
        let factIdentity =
            if fact.UsageFactId = Guid.Empty then
                $"message:{messageIdentity}"
            else
                $"fact:{fact.UsageFactId:D}"

        BillingPeriodRules.deterministicId [ "ingestion-failure"
                                             factIdentity
                                             failureCode
                                             fact.Scope.OwnerId.ToString("D")
                                             fact.Scope.OrganizationId.ToString("D")
                                             fact.Scope.RepositoryId.ToString("D")
                                             string (fact.ObservedAt.ToDateTimeUtc().Ticks) ]

/// Records durable scoped billing-relevant ingestion failure evidence before message settlement.
type IBillingIngestionFailureRecorder =
    /// Upserts one bounded active failure for a rejected fact identity.
    abstract RecordFailureAsync:
        fact: UsageFact * failureCode: string * redactedDetail: string * messageIdentity: string * cancellationToken: CancellationToken -> Task

/// Persists idempotent scoped failure evidence used by close completeness checks.
type SqlBillingIngestionFailureRecorder(connectionString: string) =
    interface IBillingIngestionFailureRecorder with
        member _.RecordFailureAsync(fact, failureCode, redactedDetail, messageIdentity, cancellationToken) =
            task {
                if String.IsNullOrWhiteSpace messageIdentity then
                    invalidArg "messageIdentity" "Billing failure evidence requires a stable broker message or correlation identity."

                let detail =
                    if String.IsNullOrWhiteSpace redactedDetail then
                        "Rejected billing-relevant usage fact."
                    else
                        redactedDetail[.. Math.Min(redactedDetail.Length, 1024) - 1]

                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use command = connection.CreateCommand()

                command.CommandText <-
                    """
DECLARE @CustomerId uniqueidentifier=(SELECT TOP(1) CustomerId FROM ops.CustomerPricingAssignment WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND @ObservedAtUtc>=EffectiveFromUtc AND (EffectiveToUtc IS NULL OR @ObservedAtUtc<EffectiveToUtc) ORDER BY EffectiveFromUtc DESC,CustomerPricingAssignmentId);
IF NOT EXISTS(SELECT 1 FROM ops.BillingIngestionFailure WHERE BillingIngestionFailureId=@FailureId AND ResolvedAtUtc IS NULL)
INSERT INTO ops.BillingIngestionFailure(BillingIngestionFailureId,UsageFactId,CustomerId,OwnerId,OrganizationId,RepositoryId,ObservedAtUtc,FailureCode,FailureDetail,CreatedAtUtc)
VALUES(@FailureId,@UsageFactId,@CustomerId,@OwnerId,@OrganizationId,@RepositoryId,@ObservedAtUtc,@FailureCode,@FailureDetail,SYSUTCDATETIME());"""

                let add name dbType value = command.Parameters.Add(name, dbType).Value <- value

                add "@FailureId" SqlDbType.UniqueIdentifier (BillingIngestionFailureIdentity.failureId fact failureCode messageIdentity)

                add "@UsageFactId" SqlDbType.UniqueIdentifier (if fact.UsageFactId = Guid.Empty then box DBNull.Value else box fact.UsageFactId)
                add "@OwnerId" SqlDbType.UniqueIdentifier fact.Scope.OwnerId
                add "@OrganizationId" SqlDbType.UniqueIdentifier fact.Scope.OrganizationId
                add "@RepositoryId" SqlDbType.UniqueIdentifier fact.Scope.RepositoryId
                add "@ObservedAtUtc" SqlDbType.DateTime2 (fact.ObservedAt.ToDateTimeUtc())
                command.Parameters.Add("@FailureCode", SqlDbType.NVarChar, 64).Value <- failureCode
                command.Parameters.Add("@FailureDetail", SqlDbType.NVarChar, 1024).Value <- detail
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            }

/// Exposes idempotent billing materialization, close, correction, and repair operations.
type IBillingPeriodService =
    /// Runs one idempotent production lifecycle and correction pass.
    abstract RunAutomaticPassAsync: nowUtc: DateTime * cancellationToken: CancellationToken -> Task
    /// Materializes UTC month periods and advances exact lifecycle boundaries.
    abstract MaterializeAndAdvanceAsync: nowUtc: DateTime * cancellationToken: CancellationToken -> Task<int>

    /// Attempts one close without any timing or completeness bypass.
    abstract TryCloseAsync:
        billingPeriodId: Guid * nowUtc: DateTime * provenance: BillingOperationProvenance option * cancellationToken: CancellationToken ->
            Task<BillingCloseOutcome>

    /// Processes pending late-fact corrections using append-only signed deltas.
    abstract ProcessCorrectionsAsync: nowUtc: DateTime * batchSize: int * cancellationToken: CancellationToken -> Task<int>

    /// Appends one explicit signed operator adjustment or reversal and preserves complete provenance.
    abstract AppendManualCorrectionAsync:
        correction: ManualBillingCorrection * provenance: BillingOperationProvenance * cancellationToken: CancellationToken -> Task<Guid>

    /// Resolves exact scoped failure evidence after a proven operator repair.
    abstract RepairFailureAsync: failureId: Guid * provenance: BillingOperationProvenance * cancellationToken: CancellationToken -> Task<bool>

/// Implements billing close and correction state transitions through transaction-owned SQL scope locks.
type SqlBillingPeriodService(connectionString: string) as this =
    let add (command: SqlCommand) name dbType value = command.Parameters.Add(name, dbType).Value <- value

    let addScope (command: SqlCommand) (scope: ChargePreviewScope) =
        add command "@CustomerId" SqlDbType.UniqueIdentifier scope.CustomerId
        add command "@OwnerId" SqlDbType.UniqueIdentifier scope.OwnerId
        add command "@OrganizationId" SqlDbType.UniqueIdentifier scope.OrganizationId
        add command "@RepositoryId" SqlDbType.UniqueIdentifier scope.RepositoryId
        add command "@PeriodFromUtc" SqlDbType.DateTime2 scope.PeriodFromUtc
        add command "@PeriodToUtc" SqlDbType.DateTime2 scope.PeriodToUtc

    let command (connection: SqlConnection) (transaction: SqlTransaction) text =
        let result = connection.CreateCommand()
        result.Transaction <- transaction
        result.CommandText <- text
        result

    let rollback (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    let recordCloseFailure billingPeriodId nowUtc code =
        task {
            try
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None
                use command = connection.CreateCommand()

                command.CommandText <-
                    "UPDATE ops.BillingPeriod SET State=1,CloseBlockedCode=@Code,CloseBlockedDetail=N'Close dependency or validation failed; inspect operational logs by correlation.',LastCloseAttemptAtUtc=@NowUtc,ConsecutiveCloseFailureCount=ConsecutiveCloseFailureCount+1 WHERE BillingPeriodId=@Id AND State=1;"

                command.Parameters.Add("@Code", SqlDbType.NVarChar, 64).Value <- code
                add command "@NowUtc" SqlDbType.DateTime2 nowUtc
                add command "@Id" SqlDbType.UniqueIdentifier billingPeriodId
                let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                return ()
            with
            | _ -> return ()
        }

    let digest (values: string seq) =
        values
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    let lockScope (connection: SqlConnection) (transaction: SqlTransaction) (scope: ChargePreviewScope) (cancellationToken: CancellationToken) =
        task {
            use lockCommand = command connection transaction OperationsBillingSql.AcquireScopeLock
            lockCommand.CommandTimeout <- 65

            let identity =
                $"ops:charge-preview:{scope.CustomerId:D}:{scope.OwnerId:D}:{scope.OrganizationId:D}:{scope.RepositoryId:D}:{scope.PeriodFromUtc.Ticks}:{scope.PeriodToUtc.Ticks}"

            let hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes identity))
            lockCommand.Parameters.Add("@LockResource", SqlDbType.NVarChar, 255).Value <- $"ops:charge-preview:{hash}"
            let! _ = lockCommand.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    let readPricedFacts (connection: SqlConnection) (transaction: SqlTransaction) (scope: ChargePreviewScope) (cancellationToken: CancellationToken) =
        task {
            use read = command connection transaction OperationsChargePreviewSql.SelectSourceAndPricing
            addScope read scope
            use! reader = read.ExecuteReaderAsync cancellationToken
            let facts = ResizeArray<ChargePreviewPricedFact>()
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
                    | Some prerequisite -> raise (ChargePreviewRebuildException(scope, usageFactId, prerequisite))
                    | None ->
                        facts.Add
                            {
                                UsageFactId = usageFactId
                                FactKind = reader.GetInt32 1
                                Quantity = reader.GetInt64 2
                                ObservedAtUtc = reader.GetDateTime 3
                                CustomerPricingAssignmentId = reader.GetGuid 4
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

            return facts |> Seq.toArray
        }

    let factsDigest (facts: ChargePreviewPricedFact array) =
        facts
        |> Seq.sortBy (fun fact -> fact.UsageFactId)
        |> Seq.map (fun fact -> $"{fact.UsageFactId:D}|{fact.FactKind}|{fact.Quantity}|{fact.ObservedAtUtc.Ticks}")
        |> digest

    let readPricingDigest (connection: SqlConnection) (transaction: SqlTransaction) (scope: ChargePreviewScope) (cancellationToken: CancellationToken) =
        task {
            use read =
                command
                    connection
                    transaction
                    """
SELECT Value FROM (
 SELECT CONCAT('assignment|',CustomerPricingAssignmentId,'|',CustomerId,'|',OwnerId,'|',OrganizationId,'|',RepositoryId,'|',PricingPlanId,'|',CONVERT(varchar(33),EffectiveFromUtc,126),'|',COALESCE(CONVERT(varchar(33),EffectiveToUtc,126),'null')) Value
 FROM ops.CustomerPricingAssignment WHERE CustomerId=@CustomerId AND OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND EffectiveFromUtc<@PeriodToUtc AND (EffectiveToUtc IS NULL OR EffectiveToUtc>@PeriodFromUtc)
 UNION ALL
 SELECT CONCAT('plan|',p.PricingPlanId,'|',p.PlanCode,'|',p.DisplayName,'|',CONVERT(varchar(33),p.EffectiveFromUtc,126),'|',COALESCE(CONVERT(varchar(33),p.EffectiveToUtc,126),'null'))
 FROM ops.PricingPlan p WHERE EXISTS(SELECT 1 FROM ops.CustomerPricingAssignment a WHERE a.CustomerId=@CustomerId AND a.OwnerId=@OwnerId AND a.OrganizationId=@OrganizationId AND a.RepositoryId=@RepositoryId AND a.PricingPlanId=p.PricingPlanId AND a.EffectiveFromUtc<@PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>@PeriodFromUtc))
 UNION ALL
 SELECT CONCAT('mapping|',BillableUsageKindMappingId,'|',FactKind,'|',BillableUsageKind,'|',DisplayName,'|',CONVERT(varchar(33),EffectiveFromUtc,126),'|',COALESCE(CONVERT(varchar(33),EffectiveToUtc,126),'null'))
 FROM ops.BillableUsageKindMapping WHERE EffectiveFromUtc<@PeriodToUtc AND (EffectiveToUtc IS NULL OR EffectiveToUtc>@PeriodFromUtc)
 UNION ALL
 SELECT CONCAT('rate|',r.PricingRateId,'|',r.PricingPlanId,'|',r.BillableUsageKind,'|',r.CurrencyCode,'|',r.UnitName,'|',r.UnitQuantity,'|',r.UnitPriceMicros,'|',CONVERT(varchar(33),r.EffectiveFromUtc,126),'|',COALESCE(CONVERT(varchar(33),r.EffectiveToUtc,126),'null'))
 FROM ops.PricingRate r WHERE r.EffectiveFromUtc<@PeriodToUtc AND (r.EffectiveToUtc IS NULL OR r.EffectiveToUtc>@PeriodFromUtc) AND EXISTS(SELECT 1 FROM ops.CustomerPricingAssignment a WHERE a.CustomerId=@CustomerId AND a.OwnerId=@OwnerId AND a.OrganizationId=@OrganizationId AND a.RepositoryId=@RepositoryId AND a.PricingPlanId=r.PricingPlanId AND a.EffectiveFromUtc<@PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>@PeriodFromUtc))
) priced ORDER BY Value;"""

            addScope read scope
            use! reader = read.ExecuteReaderAsync cancellationToken
            let values = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken
                reading <- hasRow
                if hasRow then values.Add(reader.GetString 0)

            return digest values
        }

    let readPreview (connection: SqlConnection) (transaction: SqlTransaction) (scope: ChargePreviewScope) (cancellationToken: CancellationToken) =
        task {
            use read =
                command
                    connection
                    transaction
                    "SELECT ChargePreviewLineId,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros FROM ops.ChargePreviewLine WHERE CustomerId=@CustomerId AND OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND PeriodFromUtc=@PeriodFromUtc AND PeriodToUtc=@PeriodToUtc ORDER BY ChargePreviewLineId;"

            addScope read scope
            use! reader = read.ExecuteReaderAsync cancellationToken
            let lines = ResizeArray<ChargePreviewLineEntity>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken
                reading <- hasRow

                if hasRow then
                    lines.Add(
                        ChargePreviewLineEntity(
                            ChargePreviewLineId = reader.GetGuid 0,
                            CustomerId = scope.CustomerId,
                            OwnerId = scope.OwnerId,
                            OrganizationId = scope.OrganizationId,
                            RepositoryId = scope.RepositoryId,
                            PeriodFromUtc = scope.PeriodFromUtc,
                            PeriodToUtc = scope.PeriodToUtc,
                            FactKind = reader.GetInt32 1,
                            BillableUsageKindMappingId = reader.GetGuid 2,
                            BillableUsageKind = reader.GetInt32 3,
                            CustomerPricingAssignmentId = reader.GetGuid 4,
                            PricingPlanId = reader.GetGuid 5,
                            PricingRateId = reader.GetGuid 6,
                            CurrencyCode = reader.GetString 7,
                            UnitName = reader.GetString 8,
                            UnitQuantity = reader.GetInt64 9,
                            UnitPriceMicros = reader.GetInt64 10,
                            EffectiveFromUtc = reader.GetDateTime 11,
                            EffectiveToUtc = reader.GetDateTime 12,
                            TotalQuantity = reader.GetInt64 13,
                            ChargeMicros = reader.GetInt64 14
                        )
                    )

            return lines.ToArray()
        }

    let replacePreview
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (billingPeriodId: Guid)
        (scope: ChargePreviewScope)
        (facts: ChargePreviewPricedFact array)
        (pricingHash: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let lines = ChargePreviewCalculation.buildLines scope facts
            use delete = command connection transaction OperationsChargePreviewSql.DeleteScope
            addScope delete scope
            let! _ = delete.ExecuteNonQueryAsync cancellationToken
            let mutable index = 0

            while index < lines.Length do
                let line = lines[index]

                use insert =
                    command
                        connection
                        transaction
                        "INSERT INTO ops.ChargePreviewLine(ChargePreviewLineId,CustomerId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES(@Id,@CustomerId,@OwnerId,@OrganizationId,@RepositoryId,@PeriodFromUtc,@PeriodToUtc,@FactKind,@Mapping,@BillableKind,@Assignment,@Plan,@Rate,@Currency,@Unit,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@Quantity,@Charge);"

                addScope insert scope
                add insert "@Id" SqlDbType.UniqueIdentifier line.ChargePreviewLineId
                add insert "@FactKind" SqlDbType.Int line.FactKind
                add insert "@Mapping" SqlDbType.UniqueIdentifier line.BillableUsageKindMappingId
                add insert "@BillableKind" SqlDbType.Int line.BillableUsageKind
                add insert "@Assignment" SqlDbType.UniqueIdentifier line.CustomerPricingAssignmentId
                add insert "@Plan" SqlDbType.UniqueIdentifier line.PricingPlanId
                add insert "@Rate" SqlDbType.UniqueIdentifier line.PricingRateId
                insert.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- line.CurrencyCode
                insert.Parameters.Add("@Unit", SqlDbType.NVarChar, 64).Value <- line.UnitName
                add insert "@UnitQuantity" SqlDbType.BigInt line.UnitQuantity
                add insert "@UnitPrice" SqlDbType.BigInt line.UnitPriceMicros
                add insert "@EffectiveFrom" SqlDbType.DateTime2 line.EffectiveFromUtc
                add insert "@EffectiveTo" SqlDbType.DateTime2 line.EffectiveToUtc
                add insert "@Quantity" SqlDbType.BigInt line.TotalQuantity
                add insert "@Charge" SqlDbType.BigInt line.ChargeMicros
                let! _ = insert.ExecuteNonQueryAsync cancellationToken
                index <- index + 1

            use evidence =
                command
                    connection
                    transaction
                    "MERGE ops.ChargePreviewFreshness AS target USING(SELECT @BillingPeriodId BillingPeriodId) source ON target.BillingPeriodId=source.BillingPeriodId WHEN MATCHED THEN UPDATE SET AcceptedFactsDigest=@FactsDigest,PricingDigest=@PricingDigest,PreviewCommittedAtUtc=SYSUTCDATETIME() WHEN NOT MATCHED THEN INSERT(BillingPeriodId,AcceptedFactsDigest,PricingDigest,PreviewCommittedAtUtc) VALUES(@BillingPeriodId,@FactsDigest,@PricingDigest,SYSUTCDATETIME());"

            add evidence "@BillingPeriodId" SqlDbType.UniqueIdentifier billingPeriodId
            evidence.Parameters.Add("@FactsDigest", SqlDbType.Char, 64).Value <- factsDigest facts
            evidence.Parameters.Add("@PricingDigest", SqlDbType.Char, 64).Value <- pricingHash
            let! _ = evidence.ExecuteNonQueryAsync cancellationToken
            return lines
        }

    /// Materializes recalculated correction candidates without mutating posted preview history.
    let materializeCorrectionCandidates
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: ChargePreviewScope)
        (facts: ChargePreviewPricedFact array)
        (cancellationToken: CancellationToken)
        =
        task {
            use create =
                command
                    connection
                    transaction
                    "CREATE TABLE #CorrectionExpected(FactKind int NOT NULL,BillableUsageKindMappingId uniqueidentifier NOT NULL,BillableUsageKind int NOT NULL,CustomerPricingAssignmentId uniqueidentifier NOT NULL,PricingPlanId uniqueidentifier NOT NULL,PricingRateId uniqueidentifier NOT NULL,CurrencyCode varchar(3) NOT NULL,UnitName nvarchar(64) NOT NULL,UnitQuantity bigint NOT NULL,UnitPriceMicros bigint NOT NULL,EffectiveFromUtc datetime2(7) NOT NULL,EffectiveToUtc datetime2(7) NOT NULL,Quantity bigint NOT NULL,ChargeMicros bigint NOT NULL);"

            let! _ = create.ExecuteNonQueryAsync cancellationToken
            let lines = ChargePreviewCalculation.buildLines scope facts

            for line in lines do
                use insert =
                    command
                        connection
                        transaction
                        "INSERT INTO #CorrectionExpected(FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros) VALUES(@FactKind,@Mapping,@BillableKind,@Assignment,@Plan,@Rate,@Currency,@Unit,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@Quantity,@Charge);"

                add insert "@FactKind" SqlDbType.Int line.FactKind
                add insert "@Mapping" SqlDbType.UniqueIdentifier line.BillableUsageKindMappingId
                add insert "@BillableKind" SqlDbType.Int line.BillableUsageKind
                add insert "@Assignment" SqlDbType.UniqueIdentifier line.CustomerPricingAssignmentId
                add insert "@Plan" SqlDbType.UniqueIdentifier line.PricingPlanId
                add insert "@Rate" SqlDbType.UniqueIdentifier line.PricingRateId
                insert.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- line.CurrencyCode
                insert.Parameters.Add("@Unit", SqlDbType.NVarChar, 64).Value <- line.UnitName
                add insert "@UnitQuantity" SqlDbType.BigInt line.UnitQuantity
                add insert "@UnitPrice" SqlDbType.BigInt line.UnitPriceMicros
                add insert "@EffectiveFrom" SqlDbType.DateTime2 line.EffectiveFromUtc
                add insert "@EffectiveTo" SqlDbType.DateTime2 line.EffectiveToUtc
                add insert "@Quantity" SqlDbType.BigInt line.TotalQuantity
                add insert "@Charge" SqlDbType.BigInt line.ChargeMicros
                let! _ = insert.ExecuteNonQueryAsync cancellationToken
                ()
        }

    interface IBillingPeriodService with
        member _.RunAutomaticPassAsync(nowUtc, cancellationToken) =
            task {
                let service = this :> IBillingPeriodService
                let! _ = service.MaterializeAndAdvanceAsync(nowUtc, cancellationToken)
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use candidates = connection.CreateCommand()

                candidates.CommandText <-
                    "SELECT BillingPeriodId FROM ops.BillingPeriod WHERE State=1 AND @NowUtc>=DATEADD(hour,72,PeriodToUtc) ORDER BY PeriodToUtc,BillingPeriodId;"

                add candidates "@NowUtc" SqlDbType.DateTime2 nowUtc
                use! reader = candidates.ExecuteReaderAsync cancellationToken
                let ids = ResizeArray<Guid>()
                let mutable reading = true

                while reading do
                    let! hasRow = reader.ReadAsync cancellationToken
                    reading <- hasRow
                    if hasRow then ids.Add(reader.GetGuid 0)

                do! reader.CloseAsync()
                let mutable index = 0

                while index < ids.Count do
                    let! _ = service.TryCloseAsync(ids[index], nowUtc, None, cancellationToken)
                    index <- index + 1

                let! _ = service.ProcessCorrectionsAsync(nowUtc, 100, cancellationToken)
                return ()
            }

        member _.MaterializeAndAdvanceAsync(nowUtc, cancellationToken) =
            task {
                if nowUtc.Kind <> DateTimeKind.Utc then
                    invalidArg "nowUtc" "Billing lifecycle timestamps must be UTC."

                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use command = connection.CreateCommand()

                command.CommandText <-
                    """
DECLARE @CurrentMonthFrom datetime2(7)=DATETIMEFROMPARTS(YEAR(@NowUtc),MONTH(@NowUtc),1,0,0,0,0);
;WITH AssignmentMonths AS (
 SELECT a.CustomerId,a.OwnerId,a.OrganizationId,a.RepositoryId,
        DATETIMEFROMPARTS(YEAR(a.EffectiveFromUtc),MONTH(a.EffectiveFromUtc),1,0,0,0,0) PeriodFromUtc,
        a.EffectiveFromUtc,a.EffectiveToUtc
 FROM ops.CustomerPricingAssignment a WHERE a.EffectiveFromUtc<DATEADD(month,1,@CurrentMonthFrom)
 UNION ALL
 SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,DATEADD(month,1,PeriodFromUtc),EffectiveFromUtc,EffectiveToUtc
 FROM AssignmentMonths
 WHERE DATEADD(month,1,PeriodFromUtc)<=@CurrentMonthFrom
   AND (EffectiveToUtc IS NULL OR DATEADD(month,1,PeriodFromUtc)<EffectiveToUtc))
INSERT INTO ops.BillingPeriod(BillingPeriodId,CustomerId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,State,ConsecutiveCloseFailureCount,CreatedAtUtc)
SELECT NEWID(),a.CustomerId,a.OwnerId,a.OrganizationId,a.RepositoryId,a.PeriodFromUtc,DATEADD(month,1,a.PeriodFromUtc),0,0,SYSUTCDATETIME()
FROM AssignmentMonths a
WHERE a.PeriodFromUtc<DATEADD(month,1,@CurrentMonthFrom)
  AND DATEADD(month,1,a.PeriodFromUtc)>a.EffectiveFromUtc
  AND (a.EffectiveToUtc IS NULL OR a.PeriodFromUtc<a.EffectiveToUtc)
  AND NOT EXISTS(SELECT 1 FROM ops.BillingPeriod p WHERE p.CustomerId=a.CustomerId AND p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId AND p.PeriodFromUtc=a.PeriodFromUtc AND p.PeriodToUtc=DATEADD(month,1,a.PeriodFromUtc))
GROUP BY a.CustomerId,a.OwnerId,a.OrganizationId,a.RepositoryId,a.PeriodFromUtc
OPTION(MAXRECURSION 0);
DECLARE @Inserted int=@@ROWCOUNT;
UPDATE ops.BillingPeriod SET State=1 WHERE State=0 AND @NowUtc>=DATEADD(hour,24,PeriodToUtc);
SELECT @Inserted+@@ROWCOUNT;"""

                add command "@NowUtc" SqlDbType.DateTime2 nowUtc
                let! count = command.ExecuteScalarAsync cancellationToken
                return Convert.ToInt32 count
            }

        member _.TryCloseAsync(billingPeriodId, nowUtc, provenance, cancellationToken) =
            task {
                if nowUtc.Kind <> DateTimeKind.Utc then
                    invalidArg "nowUtc" "Billing lifecycle timestamps must be UTC."

                provenance
                |> Option.iter BillingProvenance.validate

                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use! raw = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                use transaction = raw :?> SqlTransaction

                try
                    use periodCommand =
                        command
                            connection
                            transaction
                            "SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,State FROM ops.BillingPeriod WITH(UPDLOCK,HOLDLOCK) WHERE BillingPeriodId=@Id;"

                    add periodCommand "@Id" SqlDbType.UniqueIdentifier billingPeriodId
                    use! reader = periodCommand.ExecuteReaderAsync cancellationToken

                    if not (reader.Read()) then
                        invalidArg "billingPeriodId" "Billing period does not exist."

                    let scope: ChargePreviewScope =
                        {
                            CustomerId = reader.GetGuid 0
                            OwnerId = reader.GetGuid 1
                            OrganizationId = reader.GetGuid 2
                            RepositoryId = reader.GetGuid 3
                            PeriodFromUtc = DateTime.SpecifyKind(reader.GetDateTime 4, DateTimeKind.Utc)
                            PeriodToUtc = DateTime.SpecifyKind(reader.GetDateTime 5, DateTimeKind.Utc)
                        }

                    let state = enum<BillingPeriodState> (reader.GetInt32 6)
                    do! reader.CloseAsync()

                    if state = BillingPeriodState.Closed
                       || state = BillingPeriodState.Corrected then
                        do! transaction.CommitAsync cancellationToken
                        return BillingCloseOutcome.AlreadyFinal
                    elif not (BillingPeriodRules.isCloseEligible scope.PeriodToUtc nowUtc) then
                        do! transaction.CommitAsync cancellationToken
                        return BillingCloseOutcome.NotEligible
                    else
                        do! lockScope connection transaction scope cancellationToken

                        use blocker =
                            command
                                connection
                                transaction
                                "SELECT TOP(1) FailureCode FROM ops.BillingIngestionFailure WHERE ResolvedAtUtc IS NULL AND CustomerId=@CustomerId AND OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND ObservedAtUtc>=@PeriodFromUtc AND ObservedAtUtc<@PeriodToUtc;"

                        addScope blocker scope
                        let! blockerCode = blocker.ExecuteScalarAsync cancellationToken

                        if not (isNull blockerCode) then
                            use update =
                                command
                                    connection
                                    transaction
                                    "UPDATE ops.BillingPeriod SET State=1,CloseBlockedCode=@Code,CloseBlockedDetail=N'Billing-relevant ingestion evidence remains unresolved.',LastCloseAttemptAtUtc=@NowUtc,ConsecutiveCloseFailureCount=ConsecutiveCloseFailureCount+1 WHERE BillingPeriodId=@Id;"

                            add update "@Code" SqlDbType.NVarChar (string blockerCode)
                            add update "@NowUtc" SqlDbType.DateTime2 nowUtc
                            add update "@Id" SqlDbType.UniqueIdentifier billingPeriodId
                            let! _ = update.ExecuteNonQueryAsync cancellationToken
                            do! transaction.CommitAsync cancellationToken
                            return BillingCloseOutcome.Blocked(string blockerCode)
                        else
                            let! facts = readPricedFacts connection transaction scope cancellationToken
                            let factsHash = factsDigest facts
                            let! pricingHash = readPricingDigest connection transaction scope cancellationToken

                            use freshness =
                                command
                                    connection
                                    transaction
                                    "SELECT CASE WHEN AcceptedFactsDigest=@Facts AND PricingDigest=@Pricing THEN 1 ELSE 0 END FROM ops.ChargePreviewFreshness WHERE BillingPeriodId=@Id;"

                            freshness.Parameters.Add("@Facts", SqlDbType.Char, 64).Value <- factsHash
                            freshness.Parameters.Add("@Pricing", SqlDbType.Char, 64).Value <- pricingHash
                            add freshness "@Id" SqlDbType.UniqueIdentifier billingPeriodId
                            let! reusable = freshness.ExecuteScalarAsync cancellationToken

                            let! lines =
                                if
                                    not (isNull reusable)
                                    && Convert.ToInt32(reusable) = 1
                                then
                                    readPreview connection transaction scope cancellationToken
                                else
                                    replacePreview connection transaction billingPeriodId scope facts pricingHash cancellationToken

                            use post =
                                command
                                    connection
                                    transaction
                                    "INSERT INTO ops.ChargeLedgerEntry(ChargeLedgerEntryId,BillingPeriodId,EntryKind,SourceChargePreviewLineId,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,InitiatedByPrincipalId,ReasonCode,ReasonText,CorrelationId,CreatedAtUtc) SELECT NEWID(),@Id,0,l.ChargePreviewLineId,l.FactKind,l.BillableUsageKindMappingId,l.BillableUsageKind,l.CustomerPricingAssignmentId,l.PricingPlanId,l.PricingRateId,l.CurrencyCode,l.UnitName,l.UnitQuantity,l.UnitPriceMicros,l.EffectiveFromUtc,l.EffectiveToUtc,l.TotalQuantity,l.ChargeMicros,@Principal,@ReasonCode,@ReasonText,@Correlation,SYSUTCDATETIME() FROM ops.ChargePreviewLine l WHERE l.CustomerId=@CustomerId AND l.OwnerId=@OwnerId AND l.OrganizationId=@OrganizationId AND l.RepositoryId=@RepositoryId AND l.PeriodFromUtc=@PeriodFromUtc AND l.PeriodToUtc=@PeriodToUtc AND NOT EXISTS(SELECT 1 FROM ops.ChargeLedgerEntry e WHERE e.BillingPeriodId=@Id AND e.EntryKind=0 AND e.SourceChargePreviewLineId=l.ChargePreviewLineId);"

                            addScope post scope
                            add post "@Id" SqlDbType.UniqueIdentifier billingPeriodId

                            let p =
                                provenance
                                |> Option.defaultValue
                                    {
                                        InitiatedByPrincipalId = "Grace.Operations"
                                        ReasonCode = "AutomaticClose"
                                        ReasonText = "Automatic eligible period close."
                                        CorrelationId = $"billing-close:{billingPeriodId:D}"
                                    }

                            post.Parameters.Add("@Principal", SqlDbType.NVarChar, 256).Value <- p.InitiatedByPrincipalId
                            post.Parameters.Add("@ReasonCode", SqlDbType.NVarChar, 64).Value <- p.ReasonCode
                            post.Parameters.Add("@ReasonText", SqlDbType.NVarChar, 1024).Value <- p.ReasonText
                            post.Parameters.Add("@Correlation", SqlDbType.NVarChar, 128).Value <- p.CorrelationId
                            let! posted = post.ExecuteNonQueryAsync cancellationToken

                            use close =
                                command
                                    connection
                                    transaction
                                    "UPDATE ops.BillingPeriod SET State=2,ClosedAtUtc=@NowUtc,CloseBlockedCode=NULL,CloseBlockedDetail=NULL,LastCloseAttemptAtUtc=@NowUtc,ConsecutiveCloseFailureCount=0,CloseInitiatedByPrincipalId=@Principal,CloseReasonCode=@ReasonCode,CloseReasonText=@ReasonText,CloseCorrelationId=@Correlation WHERE BillingPeriodId=@Id AND State=1;"

                            add close "@NowUtc" SqlDbType.DateTime2 nowUtc
                            add close "@Principal" SqlDbType.NVarChar p.InitiatedByPrincipalId
                            add close "@ReasonCode" SqlDbType.NVarChar p.ReasonCode
                            add close "@ReasonText" SqlDbType.NVarChar p.ReasonText
                            add close "@Correlation" SqlDbType.NVarChar p.CorrelationId
                            add close "@Id" SqlDbType.UniqueIdentifier billingPeriodId
                            let! changed = close.ExecuteNonQueryAsync cancellationToken

                            if changed <> 1 then
                                invalidOp "Concurrent billing state transition prevented close."

                            do! transaction.CommitAsync cancellationToken
                            return BillingCloseOutcome.Closed posted
                with
                | :? OperationCanceledException as ex ->
                    do! rollback transaction
                    ExceptionDispatchInfo.Capture(ex).Throw()
                    return Unchecked.defaultof<_>
                | ex ->
                    do! rollback transaction

                    let code =
                        match ex with
                        | :? ChargePreviewRebuildException -> "MissingPricing"
                        | :? OverflowException -> "ArithmeticOverflow"
                        | :? SqlException -> "DependencyUnavailable"
                        | _ -> "CloseFailed"

                    do! recordCloseFailure billingPeriodId nowUtc code
                    return BillingCloseOutcome.Blocked code
            }

        member _.ProcessCorrectionsAsync(nowUtc, batchSize, cancellationToken) =
            task {
                if nowUtc.Kind <> DateTimeKind.Utc then
                    invalidArg "nowUtc" "Billing lifecycle timestamps must be UTC."

                if batchSize <= 0 then
                    invalidArg "batchSize" "Correction batch size must be positive."

                use discovery = new SqlConnection(connectionString)
                do! discovery.OpenAsync cancellationToken
                use pending = discovery.CreateCommand()

                pending.CommandText <-
                    "SELECT BillingCorrectionWorkId FROM ops.BillingCorrectionWork WHERE CompletedAtUtc IS NULL ORDER BY CreatedAtUtc,BillingCorrectionWorkId;"

                use! pendingReader = pending.ExecuteReaderAsync cancellationToken
                let workIds = ResizeArray<Guid>()
                let mutable reading = true

                while reading do
                    let! hasRow = pendingReader.ReadAsync cancellationToken
                    reading <- hasRow
                    if hasRow then workIds.Add(pendingReader.GetGuid 0)

                do! pendingReader.CloseAsync()
                let mutable completed = 0
                let mutable workIndex = 0

                while workIndex < workIds.Count && completed < batchSize do
                    use connection = new SqlConnection(connectionString)
                    do! connection.OpenAsync cancellationToken
                    use! raw = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    use transaction = raw :?> SqlTransaction

                    try
                        use work =
                            command
                                connection
                                transaction
                                "SELECT w.BillingPeriodId,w.UsageFactId,w.CorrelationId,p.CustomerId,p.OwnerId,p.OrganizationId,p.RepositoryId,p.PeriodFromUtc,p.PeriodToUtc,p.State FROM ops.BillingCorrectionWork w WITH(UPDLOCK,HOLDLOCK) JOIN ops.BillingPeriod p ON p.BillingPeriodId=w.BillingPeriodId WHERE w.BillingCorrectionWorkId=@WorkId AND w.CompletedAtUtc IS NULL;"

                        add work "@WorkId" SqlDbType.UniqueIdentifier workIds[workIndex]
                        use! reader = work.ExecuteReaderAsync cancellationToken

                        if reader.Read() then
                            let periodId = reader.GetGuid 0
                            let usageFactId = reader.GetGuid 1
                            let correlationId = reader.GetString 2

                            let scope: ChargePreviewScope =
                                {
                                    CustomerId = reader.GetGuid 3
                                    OwnerId = reader.GetGuid 4
                                    OrganizationId = reader.GetGuid 5
                                    RepositoryId = reader.GetGuid 6
                                    PeriodFromUtc = DateTime.SpecifyKind(reader.GetDateTime 7, DateTimeKind.Utc)
                                    PeriodToUtc = DateTime.SpecifyKind(reader.GetDateTime 8, DateTimeKind.Utc)
                                }

                            let state = enum<BillingPeriodState> (reader.GetInt32 9)
                            do! reader.CloseAsync()

                            if state <> BillingPeriodState.Closed
                               && state <> BillingPeriodState.Corrected then
                                invalidOp "Correction work must reference a Closed or Corrected period."

                            do! lockScope connection transaction scope cancellationToken
                            let! facts = readPricedFacts connection transaction scope cancellationToken
                            do! materializeCorrectionCandidates connection transaction scope facts cancellationToken

                            use delta =
                                command
                                    connection
                                    transaction
                                    """
;WITH Expected AS (
 SELECT FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,
        SUM(Quantity) Quantity,SUM(ChargeMicros) ChargeMicros
 FROM #CorrectionExpected
 GROUP BY FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc),
Posted AS (
 SELECT FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,
        SUM(Quantity) Quantity,SUM(ChargeMicros) ChargeMicros,MIN(ChargeLedgerEntryId) PriorId
 FROM ops.ChargeLedgerEntry WHERE BillingPeriodId=@PeriodId
 GROUP BY FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc)
INSERT INTO ops.ChargeLedgerEntry(ChargeLedgerEntryId,BillingPeriodId,EntryKind,SourceChargePreviewLineId,PriorChargeLedgerEntryId,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,InitiatedByPrincipalId,ReasonCode,ReasonText,CorrelationId,CreatedAtUtc)
SELECT NEWID(),@PeriodId,1,NULL,p.PriorId,COALESCE(e.FactKind,p.FactKind),COALESCE(e.BillableUsageKindMappingId,p.BillableUsageKindMappingId),COALESCE(e.BillableUsageKind,p.BillableUsageKind),COALESCE(e.CustomerPricingAssignmentId,p.CustomerPricingAssignmentId),COALESCE(e.PricingPlanId,p.PricingPlanId),COALESCE(e.PricingRateId,p.PricingRateId),COALESCE(e.CurrencyCode,p.CurrencyCode),COALESCE(e.UnitName,p.UnitName),COALESCE(e.UnitQuantity,p.UnitQuantity),COALESCE(e.UnitPriceMicros,p.UnitPriceMicros),COALESCE(e.EffectiveFromUtc,p.EffectiveFromUtc),COALESCE(e.EffectiveToUtc,p.EffectiveToUtc),COALESCE(e.Quantity,0)-COALESCE(p.Quantity,0),COALESCE(e.ChargeMicros,0)-COALESCE(p.ChargeMicros,0),N'Grace.Operations',N'LateUsageFact',N'Automatic correction for an accepted late usage fact.',@Correlation,SYSUTCDATETIME()
FROM Expected e FULL OUTER JOIN Posted p ON e.FactKind=p.FactKind AND e.BillableUsageKindMappingId=p.BillableUsageKindMappingId AND e.BillableUsageKind=p.BillableUsageKind AND e.CustomerPricingAssignmentId=p.CustomerPricingAssignmentId AND e.PricingPlanId=p.PricingPlanId AND e.PricingRateId=p.PricingRateId AND e.CurrencyCode=p.CurrencyCode AND e.UnitName=p.UnitName AND e.UnitQuantity=p.UnitQuantity AND e.UnitPriceMicros=p.UnitPriceMicros AND e.EffectiveFromUtc=p.EffectiveFromUtc AND e.EffectiveToUtc=p.EffectiveToUtc
WHERE COALESCE(e.Quantity,0)<>COALESCE(p.Quantity,0) OR COALESCE(e.ChargeMicros,0)<>COALESCE(p.ChargeMicros,0);"""

                            add delta "@PeriodId" SqlDbType.UniqueIdentifier periodId
                            delta.Parameters.Add("@Correlation", SqlDbType.NVarChar, 128).Value <- correlationId
                            let! inserted = delta.ExecuteNonQueryAsync cancellationToken

                            if inserted > 0 then
                                use correct =
                                    command connection transaction "UPDATE ops.BillingPeriod SET State=3 WHERE BillingPeriodId=@PeriodId AND State IN(2,3);"

                                add correct "@PeriodId" SqlDbType.UniqueIdentifier periodId
                                let! _ = correct.ExecuteNonQueryAsync cancellationToken
                                ()

                            use finish =
                                command
                                    connection
                                    transaction
                                    "UPDATE ops.BillingCorrectionWork SET CompletedAtUtc=@NowUtc WHERE BillingCorrectionWorkId=@WorkId AND UsageFactId=@UsageFactId;"

                            add finish "@NowUtc" SqlDbType.DateTime2 nowUtc
                            add finish "@WorkId" SqlDbType.UniqueIdentifier workIds[workIndex]
                            add finish "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
                            let! _ = finish.ExecuteNonQueryAsync cancellationToken
                            completed <- completed + 1
                        else
                            do! reader.CloseAsync()

                        do! transaction.CommitAsync cancellationToken
                    with
                    | :? OperationCanceledException as ex ->
                        do! rollback transaction
                        ExceptionDispatchInfo.Capture(ex).Throw()
                    | ex ->
                        do! rollback transaction
                        // A failed work item remains pending; later independent rows still receive a fair attempt.
                        ()

                    workIndex <- workIndex + 1

                return completed
            }

        member _.RepairFailureAsync(failureId, provenance, cancellationToken) =
            task {
                BillingProvenance.validate provenance
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use command = connection.CreateCommand()

                command.CommandText <-
                    "UPDATE ops.BillingIngestionFailure SET ResolvedAtUtc=SYSUTCDATETIME(),ResolvedByPrincipalId=@Principal,ResolutionReasonCode=@Code,ResolutionReasonText=@Text,ResolutionCorrelationId=@Correlation WHERE BillingIngestionFailureId=@Id AND ResolvedAtUtc IS NULL;"

                add command "@Principal" SqlDbType.NVarChar provenance.InitiatedByPrincipalId
                add command "@Code" SqlDbType.NVarChar provenance.ReasonCode
                add command "@Text" SqlDbType.NVarChar provenance.ReasonText
                add command "@Correlation" SqlDbType.NVarChar provenance.CorrelationId
                add command "@Id" SqlDbType.UniqueIdentifier failureId
                let! changed = command.ExecuteNonQueryAsync cancellationToken
                return changed = 1
            }

        member _.AppendManualCorrectionAsync(correction, provenance, cancellationToken) =
            task {
                BillingProvenance.validate provenance

                if correction.EntryKind = ChargeLedgerEntryKind.Charge then
                    invalidArg "correction" "Manual corrections must be Adjustment or Reversal entries."

                if correction.QuantityDelta = 0L
                   && correction.ChargeMicrosDelta = 0L then
                    invalidArg "correction" "A correction must carry a signed quantity or charge delta."

                if correction.EffectiveFromUtc.Kind
                   <> DateTimeKind.Utc
                   || correction.EffectiveToUtc.Kind <> DateTimeKind.Utc
                   || correction.EffectiveFromUtc
                      >= correction.EffectiveToUtc then
                    invalidArg "correction" "Correction applicability must be a non-empty UTC interval."

                let entryId = ManualBillingCorrectionIdentity.entryId correction provenance.CorrelationId

                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use! raw = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                use transaction = raw :?> SqlTransaction

                try
                    use scopeCommand =
                        command
                            connection
                            transaction
                            "SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,State FROM ops.BillingPeriod WITH(UPDLOCK,HOLDLOCK) WHERE BillingPeriodId=@Id;"

                    add scopeCommand "@Id" SqlDbType.UniqueIdentifier correction.BillingPeriodId
                    use! reader = scopeCommand.ExecuteReaderAsync cancellationToken

                    if not (reader.Read()) then
                        invalidArg "correction" "Billing period does not exist."

                    let scope: ChargePreviewScope =
                        {
                            CustomerId = reader.GetGuid 0
                            OwnerId = reader.GetGuid 1
                            OrganizationId = reader.GetGuid 2
                            RepositoryId = reader.GetGuid 3
                            PeriodFromUtc = DateTime.SpecifyKind(reader.GetDateTime 4, DateTimeKind.Utc)
                            PeriodToUtc = DateTime.SpecifyKind(reader.GetDateTime 5, DateTimeKind.Utc)
                        }

                    let state = enum<BillingPeriodState> (reader.GetInt32 6)
                    do! reader.CloseAsync()

                    if state <> BillingPeriodState.Closed
                       && state <> BillingPeriodState.Corrected then
                        invalidOp "Only Closed or Corrected periods accept manual corrections."

                    do! lockScope connection transaction scope cancellationToken

                    use insert =
                        command
                            connection
                            transaction
                            "IF NOT EXISTS(SELECT 1 FROM ops.ChargeLedgerEntry WHERE ChargeLedgerEntryId=@EntryId) INSERT INTO ops.ChargeLedgerEntry(ChargeLedgerEntryId,BillingPeriodId,EntryKind,SourceChargePreviewLineId,PriorChargeLedgerEntryId,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,InitiatedByPrincipalId,ReasonCode,ReasonText,CorrelationId,CreatedAtUtc) VALUES(@EntryId,@PeriodId,@Kind,NULL,@Prior,@FactKind,@Mapping,@BillableKind,@Assignment,@Plan,@Rate,@Currency,@Unit,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@Quantity,@Charge,@Principal,@ReasonCode,@ReasonText,@Correlation,SYSUTCDATETIME()); UPDATE ops.BillingPeriod SET State=3 WHERE BillingPeriodId=@PeriodId AND State IN(2,3);"

                    add insert "@EntryId" SqlDbType.UniqueIdentifier entryId
                    add insert "@PeriodId" SqlDbType.UniqueIdentifier correction.BillingPeriodId
                    add insert "@Kind" SqlDbType.Int (int correction.EntryKind)

                    add
                        insert
                        "@Prior"
                        SqlDbType.UniqueIdentifier
                        (correction.PriorChargeLedgerEntryId
                         |> Option.map box
                         |> Option.defaultValue DBNull.Value)

                    add insert "@FactKind" SqlDbType.Int correction.FactKind
                    add insert "@Mapping" SqlDbType.UniqueIdentifier correction.BillableUsageKindMappingId
                    add insert "@BillableKind" SqlDbType.Int correction.BillableUsageKind
                    add insert "@Assignment" SqlDbType.UniqueIdentifier correction.CustomerPricingAssignmentId
                    add insert "@Plan" SqlDbType.UniqueIdentifier correction.PricingPlanId
                    add insert "@Rate" SqlDbType.UniqueIdentifier correction.PricingRateId
                    insert.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- correction.CurrencyCode
                    insert.Parameters.Add("@Unit", SqlDbType.NVarChar, 64).Value <- correction.UnitName
                    add insert "@UnitQuantity" SqlDbType.BigInt correction.UnitQuantity
                    add insert "@UnitPrice" SqlDbType.BigInt correction.UnitPriceMicros
                    add insert "@EffectiveFrom" SqlDbType.DateTime2 correction.EffectiveFromUtc
                    add insert "@EffectiveTo" SqlDbType.DateTime2 correction.EffectiveToUtc
                    add insert "@Quantity" SqlDbType.BigInt correction.QuantityDelta
                    add insert "@Charge" SqlDbType.BigInt correction.ChargeMicrosDelta
                    insert.Parameters.Add("@Principal", SqlDbType.NVarChar, 256).Value <- provenance.InitiatedByPrincipalId
                    insert.Parameters.Add("@ReasonCode", SqlDbType.NVarChar, 64).Value <- provenance.ReasonCode
                    insert.Parameters.Add("@ReasonText", SqlDbType.NVarChar, 1024).Value <- provenance.ReasonText
                    insert.Parameters.Add("@Correlation", SqlDbType.NVarChar, 128).Value <- provenance.CorrelationId
                    let! _ = insert.ExecuteNonQueryAsync cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return entryId
                with
                | ex ->
                    do! rollback transaction
                    ExceptionDispatchInfo.Capture(ex).Throw()
                    return Unchecked.defaultof<_>
            }
