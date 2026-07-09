namespace Grace.Operations.Data

/// Provides SQL Server schema names and command text for Operations pricing tables.
[<RequireQualifiedAccess>]
module OperationsPricingSql =

    /// Names the pricing plan table without schema qualification for EF migrations.
    [<Literal>]
    let PricingPlanTableName = "PricingPlan"

    /// Names the pricing plan table.
    [<Literal>]
    let PricingPlanTable = "ops.PricingPlan"

    /// Names the effective-dated billable usage-kind mapping table without schema qualification for EF migrations.
    [<Literal>]
    let BillableUsageKindMappingTableName = "BillableUsageKindMapping"

    /// Names the effective-dated billable usage-kind mapping table.
    [<Literal>]
    let BillableUsageKindMappingTable = "ops.BillableUsageKindMapping"

    /// Names the pricing rate table without schema qualification for EF migrations.
    [<Literal>]
    let PricingRateTableName = "PricingRate"

    /// Names the pricing rate table.
    [<Literal>]
    let PricingRateTable = "ops.PricingRate"

    /// Names the customer pricing assignment table without schema qualification for EF migrations.
    [<Literal>]
    let CustomerPricingAssignmentTableName = "CustomerPricingAssignment"

    /// Names the customer pricing assignment table.
    [<Literal>]
    let CustomerPricingAssignmentTable = "ops.CustomerPricingAssignment"

    /// Limits stable pricing plan codes used by operator seed scripts and later APIs.
    [<Literal>]
    let PlanCodeMaxLength = 128

    /// Limits human-readable internal pricing labels.
    [<Literal>]
    let DisplayNameMaxLength = 200

    /// Stores ISO 4217-style currency codes without provider-cost or margin data.
    [<Literal>]
    let CurrencyCodeLength = 3

    /// Limits billing unit labels such as byte-minute before later customer preview surfaces format them.
    [<Literal>]
    let UnitNameMaxLength = 64

    /// Names the trigger that rejects overlapping pricing plan validity windows for the same plan code.
    [<Literal>]
    let PricingPlanOverlapTriggerName = "TR_ops_PricingPlan_PreventOverlap"

    /// Names the trigger that rejects overlapping usage-kind mapping windows for the same tracked fact kind.
    [<Literal>]
    let BillableUsageKindMappingOverlapTriggerName = "TR_ops_BillableUsageKindMapping_PreventOverlap"

    /// Names the trigger that rejects overlapping rate windows for the same plan and billable usage kind.
    [<Literal>]
    let PricingRateOverlapTriggerName = "TR_ops_PricingRate_PreventOverlap"

    /// Names the trigger that rejects overlapping customer assignments for the same repository scope.
    [<Literal>]
    let CustomerPricingAssignmentOverlapTriggerName = "TR_ops_CustomerPricingAssignment_PreventOverlap"

    /// Names the indexed lookup path for effective customer plan assignments.
    [<Literal>]
    let CustomerPricingAssignmentScopeIndexName = "IX_ops_CustomerPricingAssignment_ScopeEffective"

    /// Names the indexed lookup path for effective rate selection.
    [<Literal>]
    let PricingRateEffectiveIndexName = "IX_ops_PricingRate_PlanUsageKindEffective"

    /// Names the indexed lookup path for effective usage-kind mapping selection.
    [<Literal>]
    let BillableUsageKindMappingEffectiveIndexName = "IX_ops_BillableUsageKindMapping_FactKindEffective"

    /// Selects the effective customer pricing rate only when assignment, plan, billable mapping, and rate all exist.
    [<Literal>]
    let SelectEffectivePricingRate =
        """
SELECT TOP (1)
    assignment.CustomerPricingAssignmentId,
    assignment.CustomerId,
    assignment.OwnerId,
    assignment.OrganizationId,
    assignment.RepositoryId,
    plan.PricingPlanId,
    plan.PlanCode,
    mapping.BillableUsageKindMappingId,
    mapping.BillableUsageKind,
    rate.PricingRateId,
    rate.CurrencyCode,
    rate.UnitName,
    rate.UnitQuantity,
    rate.UnitPriceMicros,
    rate.EffectiveFromUtc,
    rate.EffectiveToUtc
FROM ops.CustomerPricingAssignment AS assignment WITH (READCOMMITTEDLOCK)
INNER JOIN ops.PricingPlan AS plan WITH (READCOMMITTEDLOCK)
    ON plan.PricingPlanId = assignment.PricingPlanId
INNER JOIN ops.BillableUsageKindMapping AS mapping WITH (READCOMMITTEDLOCK)
    ON mapping.FactKind = @FactKind
INNER JOIN ops.PricingRate AS rate WITH (READCOMMITTEDLOCK)
    ON rate.PricingPlanId = assignment.PricingPlanId
    AND rate.BillableUsageKind = mapping.BillableUsageKind
WHERE assignment.CustomerId = @CustomerId
AND assignment.OwnerId = @OwnerId
AND assignment.OrganizationId = @OrganizationId
AND assignment.RepositoryId = @RepositoryId
AND assignment.EffectiveFromUtc <= @ObservedAtUtc
AND (assignment.EffectiveToUtc IS NULL OR @ObservedAtUtc < assignment.EffectiveToUtc)
AND plan.EffectiveFromUtc <= @ObservedAtUtc
AND (plan.EffectiveToUtc IS NULL OR @ObservedAtUtc < plan.EffectiveToUtc)
AND mapping.EffectiveFromUtc <= @ObservedAtUtc
AND (mapping.EffectiveToUtc IS NULL OR @ObservedAtUtc < mapping.EffectiveToUtc)
AND rate.EffectiveFromUtc <= @ObservedAtUtc
AND (rate.EffectiveToUtc IS NULL OR @ObservedAtUtc < rate.EffectiveToUtc)
ORDER BY
    assignment.EffectiveFromUtc DESC,
    rate.EffectiveFromUtc DESC,
    mapping.EffectiveFromUtc DESC,
    plan.EffectiveFromUtc DESC,
    assignment.CustomerPricingAssignmentId ASC,
    rate.PricingRateId ASC;
"""
