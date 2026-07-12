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

    /// Names the owner pricing assignment table without schema qualification for EF migrations.
    [<Literal>]
    let PricingAssignmentTableName = "PricingAssignment"

    /// Names the owner pricing assignment table.
    [<Literal>]
    let PricingAssignmentTable = "ops.PricingAssignment"

    /// Limits stable pricing plan codes used by operator seed scripts and later APIs.
    [<Literal>]
    let PlanCodeMaxLength = 128

    /// Limits human-readable internal pricing labels.
    [<Literal>]
    let DisplayNameMaxLength = 200

    /// Stores ISO 4217-style currency codes without provider-cost or margin data.
    [<Literal>]
    let CurrencyCodeLength = 3

    /// Limits billing unit labels such as byte-minute before later owner preview surfaces format them.
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

    /// Names the trigger that rejects overlapping owner assignments for the same repository scope.
    [<Literal>]
    let PricingAssignmentOverlapTriggerName = "TR_ops_PricingAssignment_PreventOverlap"

    /// Names the indexed lookup path for effective owner plan assignments.
    [<Literal>]
    let PricingAssignmentScopeIndexName = "IX_ops_PricingAssignment_ScopeEffective"

    /// Names the foreign-key lookup path from owner assignments to pricing plans.
    [<Literal>]
    let PricingAssignmentPricingPlanIndexName = "IX_PricingAssignment_PricingPlanId"

    /// Names the indexed lookup path for effective rate selection.
    [<Literal>]
    let PricingRateEffectiveIndexName = "IX_ops_PricingRate_PlanUsageKindEffective"

    /// Names the indexed lookup path for effective usage-kind mapping selection.
    [<Literal>]
    let BillableUsageKindMappingEffectiveIndexName = "IX_ops_BillableUsageKindMapping_FactKindEffective"

    /// Selects the effective owner pricing rate only when assignment, plan, billable mapping, and rate all exist.
    [<Literal>]
    let SelectEffectivePricingRate =
        """
SELECT TOP (1)
    assignment.PricingAssignmentId,
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
    applicability.EffectiveFromUtc,
    applicability.EffectiveToUtc
FROM ops.PricingAssignment AS assignment WITH (READCOMMITTEDLOCK)
INNER JOIN ops.PricingPlan AS plan WITH (READCOMMITTEDLOCK)
    ON plan.PricingPlanId = assignment.PricingPlanId
INNER JOIN ops.BillableUsageKindMapping AS mapping WITH (READCOMMITTEDLOCK)
    ON mapping.FactKind = @FactKind
INNER JOIN ops.PricingRate AS rate WITH (READCOMMITTEDLOCK)
    ON rate.PricingPlanId = assignment.PricingPlanId
    AND rate.BillableUsageKind = mapping.BillableUsageKind
CROSS APPLY
(
    SELECT
        MAX(contributor.EffectiveFromUtc) AS EffectiveFromUtc,
        MIN(contributor.EffectiveToUtc) AS EffectiveToUtc
    FROM
    (
        VALUES
            (assignment.EffectiveFromUtc, assignment.EffectiveToUtc),
            (plan.EffectiveFromUtc, plan.EffectiveToUtc),
            (mapping.EffectiveFromUtc, mapping.EffectiveToUtc),
            (rate.EffectiveFromUtc, rate.EffectiveToUtc)
    ) AS contributor(EffectiveFromUtc, EffectiveToUtc)
) AS applicability
WHERE assignment.OwnerId = @OwnerId
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
    assignment.PricingAssignmentId ASC,
    rate.PricingRateId ASC;
"""
