namespace Grace.Operations.Data

open Microsoft.Data.SqlClient
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Metadata.Builders
open NodaTime
open System
open System.Data
open System.Globalization
open System.Numerics
open System.Runtime.ExceptionServices
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Defines SQL names and reviewed commands for rebuildable charge previews.
[<RequireQualifiedAccess>]
module OperationsChargePreviewSql =

    /// Names the provisional charge-preview table.
    [<Literal>]
    let TableName = "ChargePreviewLine"

    /// Names the complete logical-grain uniqueness index.
    [<Literal>]
    let GrainIndexName = "UX_ops_ChargePreviewLine_CompleteGrain"

    /// Names the scope lookup and replacement index.
    [<Literal>]
    let ScopeIndexName = "IX_ops_ChargePreviewLine_Scope"

    /// Acquires a transaction-owned exclusive lock for one hashed preview scope.
    let AcquireScopeLock =
        "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=@LockResource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=60000; IF @result < 0 THROW 51000, 'Could not serialize charge-preview rebuild scope.', 1;"

    /// Deletes only the exact preview scope being atomically replaced.
    let DeleteScope =
        "DELETE FROM ops.ChargePreviewLine WHERE CustomerId=@CustomerId AND OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND PeriodFromUtc=@PeriodFromUtc AND PeriodToUtc=@PeriodToUtc;"

    /// Reads compact immutable usage fields and every independently effective pricing prerequisite after scope locking.
    let SelectSourceAndPricing =
        """
SELECT fact.UsageFactId, fact.FactKind, fact.Quantity, fact.ObservedAtUtc,
       assignment.CustomerPricingAssignmentId, assignment.PricingPlanId AS AssignedPricingPlanId,
       plan.PricingPlanId, mapping.BillableUsageKindMappingId, mapping.BillableUsageKind,
       rate.PricingRateId, rate.CurrencyCode, rate.UnitName, rate.UnitQuantity, rate.UnitPriceMicros,
       applicability.EffectiveFromUtc, applicability.EffectiveToUtc
FROM ops.RawUsageFact AS fact
OUTER APPLY (
    SELECT TOP (1) candidate.CustomerPricingAssignmentId, candidate.PricingPlanId,
           candidate.EffectiveFromUtc, candidate.EffectiveToUtc
    FROM ops.CustomerPricingAssignment AS candidate
    WHERE candidate.CustomerId = @CustomerId AND candidate.OwnerId = @OwnerId
      AND candidate.OrganizationId = @OrganizationId AND candidate.RepositoryId = @RepositoryId
      AND candidate.EffectiveFromUtc <= fact.ObservedAtUtc
      AND (candidate.EffectiveToUtc IS NULL OR fact.ObservedAtUtc < candidate.EffectiveToUtc)
    ORDER BY candidate.EffectiveFromUtc DESC, candidate.CustomerPricingAssignmentId
) AS assignment
OUTER APPLY (
    SELECT TOP (1) candidate.PricingPlanId, candidate.EffectiveFromUtc, candidate.EffectiveToUtc
    FROM ops.PricingPlan AS candidate
    WHERE candidate.PricingPlanId = assignment.PricingPlanId
      AND candidate.EffectiveFromUtc <= fact.ObservedAtUtc
      AND (candidate.EffectiveToUtc IS NULL OR fact.ObservedAtUtc < candidate.EffectiveToUtc)
    ORDER BY candidate.EffectiveFromUtc DESC, candidate.PricingPlanId
) AS plan
OUTER APPLY (
    SELECT TOP (1) candidate.BillableUsageKindMappingId, candidate.BillableUsageKind,
           candidate.EffectiveFromUtc, candidate.EffectiveToUtc
    FROM ops.BillableUsageKindMapping AS candidate
    WHERE candidate.FactKind = fact.FactKind AND candidate.EffectiveFromUtc <= fact.ObservedAtUtc
      AND (candidate.EffectiveToUtc IS NULL OR fact.ObservedAtUtc < candidate.EffectiveToUtc)
    ORDER BY candidate.EffectiveFromUtc DESC, candidate.BillableUsageKindMappingId
) AS mapping
OUTER APPLY (
    SELECT TOP (1) candidate.PricingRateId, candidate.CurrencyCode, candidate.UnitName,
           candidate.UnitQuantity, candidate.UnitPriceMicros, candidate.EffectiveFromUtc, candidate.EffectiveToUtc
    FROM ops.PricingRate AS candidate
    WHERE candidate.PricingPlanId = plan.PricingPlanId
      AND candidate.BillableUsageKind = mapping.BillableUsageKind
      AND candidate.EffectiveFromUtc <= fact.ObservedAtUtc
      AND (candidate.EffectiveToUtc IS NULL OR fact.ObservedAtUtc < candidate.EffectiveToUtc)
    ORDER BY candidate.EffectiveFromUtc DESC, candidate.PricingRateId
) AS rate
OUTER APPLY (
    SELECT MAX(boundary.EffectiveFromUtc) AS EffectiveFromUtc,
           MIN(boundary.EffectiveToUtc) AS EffectiveToUtc
    FROM (VALUES
        (assignment.EffectiveFromUtc, ISNULL(assignment.EffectiveToUtc, @PeriodToUtc)),
        (plan.EffectiveFromUtc, ISNULL(plan.EffectiveToUtc, @PeriodToUtc)),
        (mapping.EffectiveFromUtc, ISNULL(mapping.EffectiveToUtc, @PeriodToUtc)),
        (rate.EffectiveFromUtc, ISNULL(rate.EffectiveToUtc, @PeriodToUtc)),
        (@PeriodFromUtc, @PeriodToUtc)
    ) AS boundary(EffectiveFromUtc, EffectiveToUtc)
) AS applicability
WHERE fact.OwnerId = @OwnerId AND fact.OrganizationId = @OrganizationId
  AND fact.RepositoryId = @RepositoryId AND fact.ObservedAtUtc >= @PeriodFromUtc
  AND fact.ObservedAtUtc < @PeriodToUtc
ORDER BY fact.ObservedAtUtc, fact.UsageFactId;
"""

/// Configures the persisted charge-preview model owned by Operations pricing.
[<RequireQualifiedAccess>]
module OperationsChargePreviewModel =

    /// Adds charge-preview constraints and indexes to the Operations EF model.
    let configure (modelBuilder: ModelBuilder) =
        let line = modelBuilder.Entity<ChargePreviewLineEntity>()

        line.ToTable(
            OperationsChargePreviewSql.TableName,
            OperationsUsageSql.SchemaName,
            fun (table: TableBuilder<ChargePreviewLineEntity>) ->
                table.HasCheckConstraint("CK_ops_ChargePreviewLine_PeriodRange", "[PeriodFromUtc] < [PeriodToUtc]")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_ChargePreviewLine_EffectiveRange",
                    "[PeriodFromUtc] <= [EffectiveFromUtc] AND [EffectiveFromUtc] < [EffectiveToUtc] AND [EffectiveToUtc] <= [PeriodToUtc]"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_ChargePreviewLine_UnitQuantity", "[UnitQuantity] > 0")
                |> ignore

                table.HasCheckConstraint("CK_ops_ChargePreviewLine_Amounts", "[UnitPriceMicros] >= 0 AND [TotalQuantity] >= 0 AND [ChargeMicros] >= 0")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_ChargePreviewLine_Currency",
                    "LEN([CurrencyCode]) = 3 AND [CurrencyCode] = UPPER([CurrencyCode]) AND [CurrencyCode] NOT LIKE '%[^A-Z]%'"
                )
                |> ignore
        )
        |> ignore

        line
            .HasKey([| "ChargePreviewLineId" |])
            .HasName("PK_ops_ChargePreviewLine")
        |> ignore

        line
            .Property<Guid>("ChargePreviewLineId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        for name in
            [
                "CustomerId"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
                "BillableUsageKindMappingId"
                "CustomerPricingAssignmentId"
                "PricingPlanId"
                "PricingRateId"
            ] do
            line
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        for name in
            [
                "PeriodFromUtc"
                "PeriodToUtc"
                "EffectiveFromUtc"
                "EffectiveToUtc"
            ] do
            line
                .Property<DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        line.Property<int>("FactKind").IsRequired()
        |> ignore

        line
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        line
            .Property<string>("CurrencyCode")
            .HasColumnType("varchar(3)")
            .HasMaxLength(3)
            .IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        line
            .Property<string>("UnitName")
            .HasMaxLength(OperationsPricingSql.UnitNameMaxLength)
            .IsRequired()
        |> ignore

        for name in
            [
                "UnitQuantity"
                "UnitPriceMicros"
                "TotalQuantity"
                "ChargeMicros"
            ] do
            line.Property<int64>(name).IsRequired() |> ignore

        line
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "PeriodFromUtc"
                    "PeriodToUtc"
                |]
            )
            .HasDatabaseName(OperationsChargePreviewSql.ScopeIndexName)
        |> ignore

        line
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "PeriodFromUtc"
                    "PeriodToUtc"
                    "FactKind"
                    "BillableUsageKindMappingId"
                    "BillableUsageKind"
                    "CustomerPricingAssignmentId"
                    "PricingPlanId"
                    "PricingRateId"
                    "CurrencyCode"
                    "UnitName"
                    "UnitQuantity"
                    "UnitPriceMicros"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName(OperationsChargePreviewSql.GrainIndexName)
            .IsUnique()
        |> ignore

/// Identifies one explicit customer/repository/half-open UTC preview rebuild scope.
type ChargePreviewScope = { CustomerId: Guid; OwnerId: Guid; OrganizationId: Guid; RepositoryId: Guid; PeriodFromUtc: DateTime; PeriodToUtc: DateTime }

/// Names an independently required pricing prerequisite that was absent for a usage fact.
[<RequireQualifiedAccess>]
type MissingPricingPrerequisite =
    | Assignment
    | Plan
    | Mapping
    | Rate

/// Reports why a complete charge-preview candidate could not be built.
type ChargePreviewRebuildException(scope: ChargePreviewScope, usageFactId: Guid, prerequisite: MissingPricingPrerequisite) =
    inherit InvalidOperationException($"Charge-preview rebuild for customer {scope.CustomerId}, repository {scope.RepositoryId}, period [{scope.PeriodFromUtc:o}, {scope.PeriodToUtc:o}) is missing pricing {prerequisite} for usage fact {usageFactId}.")
    /// Gets the rebuild scope that remains unchanged after this failure.
    member _.Scope = scope
    /// Gets the usage fact whose complete pricing could not be resolved.
    member _.UsageFactId = usageFactId
    /// Gets the independently missing pricing prerequisite.
    member _.Prerequisite = prerequisite

/// Carries compact usage and complete pricing fields read from SQL for one immutable source fact.
type ChargePreviewPricedFact =
    {
        UsageFactId: Guid
        FactKind: int
        Quantity: int64
        ObservedAtUtc: DateTime
        CustomerPricingAssignmentId: Guid
        BillableUsageKindMappingId: Guid
        BillableUsageKind: int
        PricingPlanId: Guid
        PricingRateId: Guid
        CurrencyCode: string
        UnitName: string
        UnitQuantity: int64
        UnitPriceMicros: int64
        EffectiveFromUtc: DateTime
        EffectiveToUtc: DateTime
    }

/// Builds deterministic charge-preview lines from compact usage facts and complete pricing.
[<RequireQualifiedAccess>]
module ChargePreviewCalculation =

    /// Identifies the first missing prerequisite in dependency order for clear rebuild diagnostics.
    let missingPrerequisite hasAssignment hasPlan hasMapping hasRate =
        if not hasAssignment then Some MissingPricingPrerequisite.Assignment
        elif not hasPlan then Some MissingPricingPrerequisite.Plan
        elif not hasMapping then Some MissingPricingPrerequisite.Mapping
        elif not hasRate then Some MissingPricingPrerequisite.Rate
        else None

    /// Calculates one whole-micro charge with checked exact arithmetic and midpoint rounding away from zero.
    let calculateChargeMicros totalQuantity unitPriceMicros unitQuantity =
        if totalQuantity < 0L
           || unitPriceMicros < 0L
           || unitQuantity <= 0L then
            invalidArg "pricing" "Charge-preview quantities and prices must be non-negative and unit quantity must be positive."

        let numerator =
            BigInteger(totalQuantity)
            * BigInteger(unitPriceMicros)

        let denominator = BigInteger(unitQuantity)
        let quotient, remainder = BigInteger.DivRem(numerator, denominator)
        let rounded = if remainder * 2I >= denominator then quotient + 1I else quotient

        if rounded > BigInteger(Int64.MaxValue) then
            raise (OverflowException("Charge-preview charge exceeds Int64 whole-micro storage."))

        int64 rounded

    /// Derives a stable GUID from the complete logical line grain without depending on insertion order.
    let lineId (scope: ChargePreviewScope) (fact: ChargePreviewPricedFact) =
        let storedUtcTicks (value: DateTime) =
            if value.Kind = DateTimeKind.Local then
                invalidArg "fact" "Charge-preview applicability timestamps cannot be local time."

            value.Ticks.ToString(CultureInfo.InvariantCulture)

        let canonical =
            String.Join(
                "|",
                [|
                    scope.CustomerId.ToString("D")
                    scope.OwnerId.ToString("D")
                    scope.OrganizationId.ToString("D")
                    scope.RepositoryId.ToString("D")
                    storedUtcTicks scope.PeriodFromUtc
                    storedUtcTicks scope.PeriodToUtc
                    fact.FactKind.ToString(CultureInfo.InvariantCulture)
                    fact.BillableUsageKindMappingId.ToString("D")
                    fact.BillableUsageKind.ToString(CultureInfo.InvariantCulture)
                    fact.CustomerPricingAssignmentId.ToString("D")
                    fact.PricingPlanId.ToString("D")
                    fact.PricingRateId.ToString("D")
                    fact.CurrencyCode
                    fact.UnitName
                    fact.UnitQuantity.ToString(CultureInfo.InvariantCulture)
                    fact.UnitPriceMicros.ToString(CultureInfo.InvariantCulture)
                    storedUtcTicks fact.EffectiveFromUtc
                    storedUtcTicks fact.EffectiveToUtc
                |]
            )

        let hash = SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        Guid(hash.AsSpan(0, 16))

    /// Aggregates duplicate-safe source facts by complete applicability identity and rounds once per completed line.
    let buildLines scope facts =
        if facts
           |> Seq.exists (fun fact -> fact.Quantity < 0L) then
            invalidArg "facts" "Charge-preview source quantities cannot be negative."

        facts
        |> Seq.distinctBy (fun fact -> fact.UsageFactId)
        |> Seq.groupBy (fun fact ->
            fact.FactKind,
            fact.BillableUsageKindMappingId,
            fact.BillableUsageKind,
            fact.CustomerPricingAssignmentId,
            fact.PricingPlanId,
            fact.PricingRateId,
            fact.CurrencyCode,
            fact.UnitName,
            fact.UnitQuantity,
            fact.UnitPriceMicros,
            fact.EffectiveFromUtc,
            fact.EffectiveToUtc)
        |> Seq.map (fun (_, grouped) ->
            let first = Seq.head grouped

            let total =
                grouped
                |> Seq.fold (fun value fact -> value + BigInteger(fact.Quantity)) BigInteger.Zero

            if total < BigInteger.Zero
               || total > BigInteger(Int64.MaxValue) then
                raise (OverflowException("Charge-preview summed quantity exceeds Int64 storage."))

            let quantity = int64 total
            let entity = ChargePreviewLineEntity()
            entity.ChargePreviewLineId <- lineId scope first
            entity.CustomerId <- scope.CustomerId
            entity.OwnerId <- scope.OwnerId
            entity.OrganizationId <- scope.OrganizationId
            entity.RepositoryId <- scope.RepositoryId
            entity.PeriodFromUtc <- scope.PeriodFromUtc
            entity.PeriodToUtc <- scope.PeriodToUtc
            entity.FactKind <- first.FactKind
            entity.BillableUsageKindMappingId <- first.BillableUsageKindMappingId
            entity.BillableUsageKind <- first.BillableUsageKind
            entity.CustomerPricingAssignmentId <- first.CustomerPricingAssignmentId
            entity.PricingPlanId <- first.PricingPlanId
            entity.PricingRateId <- first.PricingRateId
            entity.CurrencyCode <- first.CurrencyCode
            entity.UnitName <- first.UnitName
            entity.UnitQuantity <- first.UnitQuantity
            entity.UnitPriceMicros <- first.UnitPriceMicros
            entity.EffectiveFromUtc <- first.EffectiveFromUtc
            entity.EffectiveToUtc <- first.EffectiveToUtc
            entity.TotalQuantity <- quantity
            entity.ChargeMicros <- calculateChargeMicros quantity first.UnitPriceMicros first.UnitQuantity
            entity)
        |> Seq.sortBy (fun line -> line.ChargePreviewLineId)
        |> Seq.toArray

/// Exposes the callable provisional charge-preview rebuild used by later billing leaves.
type IChargePreviewRebuilder =
    /// Atomically replaces one explicit preview scope and returns its complete committed lines.
    abstract RebuildAsync: scope: ChargePreviewScope * cancellationToken: CancellationToken -> Task<ChargePreviewLineEntity array>

/// Rebuilds charge-preview lines from compact SQL facts under a transaction-owned same-scope lock.
type SqlChargePreviewRebuilder(connectionString: string) =

    let addScopeParameters (command: SqlCommand) scope =
        command.Parameters.Add("@CustomerId", SqlDbType.UniqueIdentifier).Value <- scope.CustomerId
        command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
        command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
        command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
        command.Parameters.Add("@PeriodFromUtc", SqlDbType.DateTime2).Value <- scope.PeriodFromUtc
        command.Parameters.Add("@PeriodToUtc", SqlDbType.DateTime2).Value <- scope.PeriodToUtc

    let rollbackIgnoringFailure (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    let validateScope scope =
        if scope.PeriodFromUtc.Kind <> DateTimeKind.Utc
           || scope.PeriodToUtc.Kind <> DateTimeKind.Utc
           || scope.PeriodFromUtc >= scope.PeriodToUtc then
            invalidArg "scope" "Charge-preview periods must be non-empty UTC half-open intervals."

    interface IChargePreviewRebuilder with
        member _.RebuildAsync(scope, cancellationToken) =
            task {
                validateScope scope
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync cancellationToken
                use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                use transaction = rawTransaction :?> SqlTransaction

                try
                    use lockCommand = connection.CreateCommand()
                    lockCommand.Transaction <- transaction

                    lockCommand.CommandText <- OperationsChargePreviewSql.AcquireScopeLock
                    lockCommand.CommandTimeout <- 65

                    let lockIdentity =
                        $"ops:charge-preview:{scope.CustomerId:D}:{scope.OwnerId:D}:{scope.OrganizationId:D}:{scope.RepositoryId:D}:{scope.PeriodFromUtc.Ticks}:{scope.PeriodToUtc.Ticks}"

                    let lockHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes lockIdentity))
                    lockCommand.Parameters.Add("@LockResource", SqlDbType.NVarChar, 255).Value <- $"ops:charge-preview:{lockHash}"
                    let! _ = lockCommand.ExecuteNonQueryAsync cancellationToken

                    use readCommand = connection.CreateCommand()
                    readCommand.Transaction <- transaction
                    readCommand.CommandText <- OperationsChargePreviewSql.SelectSourceAndPricing
                    addScopeParameters readCommand scope
                    use! reader = readCommand.ExecuteReaderAsync cancellationToken
                    let facts = ResizeArray<ChargePreviewPricedFact>()
                    let mutable hasRow = true

                    while hasRow do
                        let! nextRow = reader.ReadAsync cancellationToken
                        hasRow <- nextRow

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
                            | None -> ()

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

                    do! reader.CloseAsync()
                    let lines = ChargePreviewCalculation.buildLines scope facts

                    use deleteCommand = connection.CreateCommand()
                    deleteCommand.Transaction <- transaction

                    deleteCommand.CommandText <- OperationsChargePreviewSql.DeleteScope

                    addScopeParameters deleteCommand scope
                    let! _ = deleteCommand.ExecuteNonQueryAsync cancellationToken

                    for line in lines do
                        use insert = connection.CreateCommand()
                        insert.Transaction <- transaction

                        insert.CommandText <-
                            "INSERT INTO ops.ChargePreviewLine (ChargePreviewLineId,CustomerId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES (@Id,@CustomerId,@OwnerId,@OrganizationId,@RepositoryId,@PeriodFromUtc,@PeriodToUtc,@FactKind,@MappingId,@BillableKind,@AssignmentId,@PlanId,@RateId,@Currency,@UnitName,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@TotalQuantity,@Charge);"

                        addScopeParameters insert scope
                        let add name dbType value = insert.Parameters.Add(name, dbType).Value <- value
                        add "@Id" SqlDbType.UniqueIdentifier line.ChargePreviewLineId
                        add "@FactKind" SqlDbType.Int line.FactKind
                        add "@MappingId" SqlDbType.UniqueIdentifier line.BillableUsageKindMappingId
                        add "@BillableKind" SqlDbType.Int line.BillableUsageKind
                        add "@AssignmentId" SqlDbType.UniqueIdentifier line.CustomerPricingAssignmentId
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
                        ()

                    do! transaction.CommitAsync cancellationToken
                    return lines
                with
                | ex ->
                    do! rollbackIgnoringFailure transaction
                    ExceptionDispatchInfo.Capture(ex).Throw()
                    return Unchecked.defaultof<_>
            }
