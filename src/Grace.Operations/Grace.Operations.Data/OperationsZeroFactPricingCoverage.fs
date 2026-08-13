namespace Grace.Operations.Data

open Grace.Types.Usage
open Microsoft.Data.SqlClient
open System
open System.Data
open System.Globalization
open System.Threading
open System.Threading.Tasks

/// Reports whether every supported usage-fact kind has a complete pricing grain throughout one empty billing month.
type internal ZeroFactPricingCoverageResult =
    | Complete
    | Incomplete of diagnostic: string

/// Evaluates collective effective-dated pricing coverage using the connection and transaction that already hold a billing scope lock.
type internal IZeroFactPricingCoverage =
    /// Checks every supported fact kind without creating any durable coverage state.
    abstract EvaluateAsync:
        connection: SqlConnection * transaction: SqlTransaction * scope: BillingCompletenessScope * cancellationToken: CancellationToken ->
            Task<ZeroFactPricingCoverageResult>

/// Evaluates the current SQL pricing catalog at every effective boundary that can affect an empty period close.
type internal SqlZeroFactPricingCoverage() =

    /// Creates a SQL command tied to the already locked close transaction.
    let command (connection: SqlConnection) (transaction: SqlTransaction) text =
        let value = connection.CreateCommand()
        value.Transaction <- transaction
        value.CommandText <- text
        value

    /// Adds the exact half-open billing scope used by every coverage query.
    let addScope (command: SqlCommand) (scope: BillingCompletenessScope) =
        command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
        command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
        command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
        command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

        command.Parameters.Add("@NextMonthStartUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
            .ToDateTimeUtc()

    /// Renders a bounded retry diagnostic from the first uncovered grain.
    let diagnostic layer (factKind: UsageFactKind) (boundary: DateTime) =
        let timestamp = boundary.ToString("O", CultureInfo.InvariantCulture)
        $"{layer}:{factKind}:{timestamp}"

    /// Reads every time at which one selected pricing prerequisite can begin or end within the billing month.
    let boundariesAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (factKind: UsageFactKind)
        (cancellationToken: CancellationToken)
        =
        task {
            use read =
                command
                    connection
                    transaction
                    """
WITH BoundaryRows AS
(
    SELECT @MonthStartUtc AS BoundaryUtc
    UNION ALL SELECT @NextMonthStartUtc
    UNION ALL SELECT assignment.EffectiveFromUtc FROM ops.PricingAssignment AS assignment
        WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
          AND assignment.EffectiveFromUtc < @NextMonthStartUtc
          AND (assignment.EffectiveToUtc IS NULL OR assignment.EffectiveToUtc > @MonthStartUtc)
    UNION ALL SELECT assignment.EffectiveToUtc FROM ops.PricingAssignment AS assignment
        WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
          AND assignment.EffectiveToUtc IS NOT NULL AND assignment.EffectiveToUtc > @MonthStartUtc AND assignment.EffectiveToUtc < @NextMonthStartUtc
    UNION ALL SELECT pricingPlan.EffectiveFromUtc FROM ops.PricingPlan AS pricingPlan INNER JOIN ops.PricingAssignment AS assignment ON assignment.PricingPlanId=pricingPlan.PricingPlanId
        WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
          AND pricingPlan.EffectiveFromUtc < @NextMonthStartUtc AND (pricingPlan.EffectiveToUtc IS NULL OR pricingPlan.EffectiveToUtc > @MonthStartUtc)
    UNION ALL SELECT pricingPlan.EffectiveToUtc FROM ops.PricingPlan AS pricingPlan INNER JOIN ops.PricingAssignment AS assignment ON assignment.PricingPlanId=pricingPlan.PricingPlanId
        WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
          AND pricingPlan.EffectiveToUtc IS NOT NULL AND pricingPlan.EffectiveToUtc > @MonthStartUtc AND pricingPlan.EffectiveToUtc < @NextMonthStartUtc
    UNION ALL SELECT mapping.EffectiveFromUtc FROM ops.BillableUsageKindMapping AS mapping
        WHERE mapping.FactKind=@FactKind AND mapping.EffectiveFromUtc < @NextMonthStartUtc
          AND (mapping.EffectiveToUtc IS NULL OR mapping.EffectiveToUtc > @MonthStartUtc)
    UNION ALL SELECT mapping.EffectiveToUtc FROM ops.BillableUsageKindMapping AS mapping
        WHERE mapping.FactKind=@FactKind AND mapping.EffectiveToUtc IS NOT NULL
          AND mapping.EffectiveToUtc > @MonthStartUtc AND mapping.EffectiveToUtc < @NextMonthStartUtc
    UNION ALL SELECT rate.EffectiveFromUtc FROM ops.PricingRate AS rate
        INNER JOIN ops.PricingAssignment AS assignment ON assignment.PricingPlanId=rate.PricingPlanId
        INNER JOIN ops.BillableUsageKindMapping AS mapping ON mapping.BillableUsageKind=rate.BillableUsageKind
        WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
          AND mapping.FactKind=@FactKind AND rate.EffectiveFromUtc < @NextMonthStartUtc
          AND (rate.EffectiveToUtc IS NULL OR rate.EffectiveToUtc > @MonthStartUtc)
    UNION ALL SELECT rate.EffectiveToUtc FROM ops.PricingRate AS rate
        INNER JOIN ops.PricingAssignment AS assignment ON assignment.PricingPlanId=rate.PricingPlanId
        INNER JOIN ops.BillableUsageKindMapping AS mapping ON mapping.BillableUsageKind=rate.BillableUsageKind
        WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
          AND mapping.FactKind=@FactKind AND rate.EffectiveToUtc IS NOT NULL
          AND rate.EffectiveToUtc > @MonthStartUtc AND rate.EffectiveToUtc < @NextMonthStartUtc
)
SELECT DISTINCT BoundaryUtc FROM BoundaryRows WHERE BoundaryUtc IS NOT NULL ORDER BY BoundaryUtc;
"""

            addScope read scope
            read.Parameters.Add("@FactKind", SqlDbType.Int).Value <- int factKind
            use! reader = read.ExecuteReaderAsync cancellationToken
            let values = ResizeArray<DateTime>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken
                reading <- hasRow

                if hasRow then values.Add(reader.GetDateTime 0)

            return values |> Seq.toList
        }

    /// Selects the same assignment row that effective fact pricing would select at one timestamp.
    let selectedAssignmentAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (boundary: DateTime)
        (cancellationToken: CancellationToken)
        =
        task {
            use read =
                command
                    connection
                    transaction
                    "SELECT TOP (1) PricingPlanId FROM ops.PricingAssignment WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND EffectiveFromUtc<=@ObservedAtUtc AND (EffectiveToUtc IS NULL OR @ObservedAtUtc<EffectiveToUtc) ORDER BY EffectiveFromUtc DESC,PricingAssignmentId ASC;"

            addScope read scope
            read.Parameters.Add("@ObservedAtUtc", SqlDbType.DateTime2).Value <- boundary
            let! value = read.ExecuteScalarAsync cancellationToken
            return if isNull value || Convert.IsDBNull value then None else Some(value :?> Guid)
        }

    /// Selects the referenced effective plan using the catalog's stable latest-start ordering.
    let selectedPlanAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (pricingPlanId: Guid)
        (boundary: DateTime)
        (cancellationToken: CancellationToken)
        =
        task {
            use read =
                command
                    connection
                    transaction
                    "SELECT TOP (1) PricingPlanId FROM ops.PricingPlan WHERE PricingPlanId=@PricingPlanId AND EffectiveFromUtc<=@ObservedAtUtc AND (EffectiveToUtc IS NULL OR @ObservedAtUtc<EffectiveToUtc) ORDER BY EffectiveFromUtc DESC,PricingPlanId ASC;"

            read.Parameters.Add("@PricingPlanId", SqlDbType.UniqueIdentifier).Value <- pricingPlanId
            read.Parameters.Add("@ObservedAtUtc", SqlDbType.DateTime2).Value <- boundary
            let! value = read.ExecuteScalarAsync cancellationToken
            return if isNull value || Convert.IsDBNull value then None else Some(value :?> Guid)
        }

    /// Selects the effective mapping for the required supported fact kind.
    let selectedMappingAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (factKind: UsageFactKind)
        (boundary: DateTime)
        (cancellationToken: CancellationToken)
        =
        task {
            use read =
                command
                    connection
                    transaction
                    "SELECT TOP (1) BillableUsageKind FROM ops.BillableUsageKindMapping WHERE FactKind=@FactKind AND EffectiveFromUtc<=@ObservedAtUtc AND (EffectiveToUtc IS NULL OR @ObservedAtUtc<EffectiveToUtc) ORDER BY EffectiveFromUtc DESC,BillableUsageKindMappingId ASC;"

            read.Parameters.Add("@FactKind", SqlDbType.Int).Value <- int factKind
            read.Parameters.Add("@ObservedAtUtc", SqlDbType.DateTime2).Value <- boundary
            let! value = read.ExecuteScalarAsync cancellationToken

            return
                if isNull value || Convert.IsDBNull value then
                    None
                else
                    Some(Convert.ToInt32(value, CultureInfo.InvariantCulture))
        }

    /// Selects the effective rate for the exact selected plan and billable usage kind.
    let selectedRateAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (pricingPlanId: Guid)
        (billableUsageKind: int)
        (boundary: DateTime)
        (cancellationToken: CancellationToken)
        =
        task {
            use read =
                command
                    connection
                    transaction
                    "SELECT TOP (1) PricingRateId FROM ops.PricingRate WHERE PricingPlanId=@PricingPlanId AND BillableUsageKind=@BillableUsageKind AND EffectiveFromUtc<=@ObservedAtUtc AND (EffectiveToUtc IS NULL OR @ObservedAtUtc<EffectiveToUtc) ORDER BY EffectiveFromUtc DESC,PricingRateId ASC;"

            read.Parameters.Add("@PricingPlanId", SqlDbType.UniqueIdentifier).Value <- pricingPlanId
            read.Parameters.Add("@BillableUsageKind", SqlDbType.Int).Value <- billableUsageKind
            read.Parameters.Add("@ObservedAtUtc", SqlDbType.DateTime2).Value <- boundary
            let! value = read.ExecuteScalarAsync cancellationToken
            return not (isNull value || Convert.IsDBNull value)
        }

    /// Finds the first missing layer for a supported fact kind at a deterministic month boundary.
    let firstIncompleteBoundaryAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (factKind: UsageFactKind)
        (boundary: DateTime)
        (cancellationToken: CancellationToken)
        =
        task {
            let! assignment = selectedAssignmentAsync connection transaction scope boundary cancellationToken

            match assignment with
            | None -> return Some(diagnostic "MissingAssignment" factKind boundary)
            | Some pricingPlanId ->
                let! plan = selectedPlanAsync connection transaction pricingPlanId boundary cancellationToken

                match plan with
                | None -> return Some(diagnostic "MissingPricingPlan" factKind boundary)
                | Some _ ->
                    let! mapping = selectedMappingAsync connection transaction factKind boundary cancellationToken

                    match mapping with
                    | None -> return Some(diagnostic "MissingMapping" factKind boundary)
                    | Some billableUsageKind ->
                        let! hasRate = selectedRateAsync connection transaction pricingPlanId billableUsageKind boundary cancellationToken
                        return if hasRate then None else Some(diagnostic "MissingRate" factKind boundary)
        }

    interface IZeroFactPricingCoverage with
        member _.EvaluateAsync(connection, transaction, scope, cancellationToken) =
            task {
                let requiredFactKinds = UsageFact.SupportedV1FactKinds |> Array.sortBy int
                let mutable result = Complete
                let mutable factKindIndex = 0

                while factKindIndex < requiredFactKinds.Length
                      && result = Complete do
                    let factKind = requiredFactKinds[factKindIndex]
                    let! boundaries = boundariesAsync connection transaction scope factKind cancellationToken
                    let mutable boundaryIndex = 0

                    while boundaryIndex < boundaries.Length
                          && result = Complete do
                        let! missing = firstIncompleteBoundaryAsync connection transaction scope factKind boundaries[boundaryIndex] cancellationToken

                        match missing with
                        | Some diagnostic -> result <- Incomplete diagnostic
                        | None -> boundaryIndex <- boundaryIndex + 1

                    factKindIndex <- factKindIndex + 1

                return result
            }
