/*
Seeds the first Operations pricing foundation rows without assigning any owner to a plan.

This script intentionally creates:
- one effective pricing plan shell,
- one explicit billable mapping for UsageFactKind.RepositoryStorageBytesMinute,
- one owner price for that mapped usage kind.

Tracked usage remains non-billable for owners until an explicit row exists in ops.PricingAssignment.
*/

DECLARE @PlanId uniqueidentifier = '57400000-0000-0000-0000-000000000001';
DECLARE @MappingId uniqueidentifier = '57400000-0000-0000-0000-000000000002';
DECLARE @RateId uniqueidentifier = '57400000-0000-0000-0000-000000000003';
DECLARE @EffectiveFromUtc datetime2(7) = '2026-07-01T00:00:00.0000000';
DECLARE @RepositoryStorageBytesMinuteFactKind int = 1;
DECLARE @RepositoryStorageBillableUsageKind int = 1;

IF NOT EXISTS
(
    SELECT 1
    FROM ops.PricingPlan
    WHERE PricingPlanId = @PlanId
)
BEGIN
    INSERT INTO ops.PricingPlan
    (
        PricingPlanId,
        PlanCode,
        DisplayName,
        EffectiveFromUtc
    )
    VALUES
    (
        @PlanId,
        N'grace-standard-v1',
        N'Grace Standard v1',
        @EffectiveFromUtc
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM ops.BillableUsageKindMapping
    WHERE BillableUsageKindMappingId = @MappingId
)
BEGIN
    INSERT INTO ops.BillableUsageKindMapping
    (
        BillableUsageKindMappingId,
        FactKind,
        BillableUsageKind,
        DisplayName,
        EffectiveFromUtc
    )
    VALUES
    (
        @MappingId,
        @RepositoryStorageBytesMinuteFactKind,
        @RepositoryStorageBillableUsageKind,
        N'Repository Storage',
        @EffectiveFromUtc
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM ops.PricingRate
    WHERE PricingRateId = @RateId
)
BEGIN
    INSERT INTO ops.PricingRate
    (
        PricingRateId,
        PricingPlanId,
        BillableUsageKind,
        CurrencyCode,
        UnitName,
        UnitQuantity,
        UnitPriceMicros,
        EffectiveFromUtc
    )
    VALUES
    (
        @RateId,
        @PlanId,
        @RepositoryStorageBillableUsageKind,
        'USD',
        N'byte-minute',
        1073741824,
        0,
        @EffectiveFromUtc
    );
END;
