namespace Grace.Operations.Data

open System

/// Represents an immutable operational usage fact row managed by the Operations EF model.
[<AllowNullLiteral>]
type RawUsageFactEntity() =

    /// Stores the durable idempotency key supplied by the UsageFact contract.
    member val UsageFactId = Guid.Empty with get, set

    /// Stores the exact accepted broker payload while the fact remains inside the hot SQL window.
    member val RawPayload: byte array = Array.empty with get, set

    /// Stores the request or workflow correlation identifier associated with the fact.
    member val CorrelationId = String.Empty with get, set

    /// Stores the persisted integer representation of the UsageFact kind.
    member val FactKind = 0 with get, set

    /// Stores the Grace owner scope for the measured repository resource.
    member val OwnerId = Guid.Empty with get, set

    /// Stores the Grace organization scope for the measured repository resource.
    member val OrganizationId = Guid.Empty with get, set

    /// Stores the Grace repository scope for the measured resource.
    member val RepositoryId = Guid.Empty with get, set

    /// Stores the storage pool identity using the Operations case-sensitive collation.
    member val StoragePoolId = String.Empty with get, set

    /// Stores the measured resource quantity for the fact.
    member val Quantity = 0L with get, set

    /// Stores the UTC minute timestamp associated with the fact.
    member val ObservedAtUtc = DateTime.MinValue with get, set

    /// Stores the hot/cold archive state for the raw payload.
    member val ArchiveState = 0 with get, set

    /// Stores the deterministic Blob name that owns the archived raw payload bytes.
    member val ArchiveBlobName: string = null with get, set

    /// Stores the SHA-256 checksum for the compressed archive Blob bytes.
    member val ArchiveChecksumSha256Hex: string = null with get, set

    /// Stores the exact compressed archive Blob byte length verified before hot SQL cleanup.
    member val ArchiveByteLength = Nullable<int64>() with get, set

    /// Stores when Blob authority was verified and recorded in SQL.
    member val ArchiveVerifiedAtUtc = Nullable<DateTime>() with get, set

    /// Stores when the hot raw payload was cleared after Blob authority was verified.
    member val ArchivedAtUtc = Nullable<DateTime>() with get, set

    /// Stores when a temporarily restored archived payload must return to cold SQL state.
    member val RehydrationExpiresAtUtc = Nullable<DateTime>() with get, set

    /// Stores the latest redacted row-scoped archive failure summary for operator inspection.
    member val LastArchiveFailureReason: string = null with get, set

    /// Stores when the latest row-scoped archive failure was recorded.
    member val LastArchiveFailureAtUtc = Nullable<DateTime>() with get, set

    /// Stores the number of consecutive archive failures since the latest successful retry or operator repair.
    member val ArchiveFailureCount = 0 with get, set

    /// Stores when repeated archive failures retired the row from automatic retries pending operator repair.
    member val ArchiveRetiredAtUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the raw fact row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Represents one repository resource aggregate row for a UTC minute.
[<AllowNullLiteral>]
type UsageAggregateMinuteEntity() =

    /// Stores the persisted integer representation of the UsageFact kind.
    member val FactKind = 0 with get, set

    /// Stores the Grace owner scope for the aggregate row.
    member val OwnerId = Guid.Empty with get, set

    /// Stores the Grace organization scope for the aggregate row.
    member val OrganizationId = Guid.Empty with get, set

    /// Stores the Grace repository scope for the aggregate row.
    member val RepositoryId = Guid.Empty with get, set

    /// Stores the storage pool identity using the Operations case-sensitive collation.
    member val StoragePoolId = String.Empty with get, set

    /// Stores the UTC minute bucket represented by this aggregate row.
    member val BucketStartUtc = DateTime.MinValue with get, set

    /// Stores the accumulated resource quantity for the aggregate key.
    member val Quantity = 0L with get, set

    /// Stores the SQL-created or SQL-updated UTC timestamp for the aggregate row.
    member val UpdatedAtUtc = DateTime.MinValue with get, set

/// Represents one effective-dated customer pricing plan.
[<AllowNullLiteral>]
type PricingPlanEntity() =

    /// Stores the durable pricing plan identity used by assignments and rates.
    member val PricingPlanId = Guid.Empty with get, set

    /// Stores the operator-stable pricing plan code.
    member val PlanCode = String.Empty with get, set

    /// Stores the internal display label used by Operations tools.
    member val DisplayName = String.Empty with get, set

    /// Stores when the plan can first be selected for effective pricing.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the plan selection window, or null for open-ended plans.
    member val EffectiveToUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the pricing plan row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Represents an effective-dated mapping from a tracked usage fact kind to a billable line item kind.
[<AllowNullLiteral>]
type BillableUsageKindMappingEntity() =

    /// Stores the durable billable mapping identity used for audit and seed maintenance.
    member val BillableUsageKindMappingId = Guid.Empty with get, set

    /// Stores the tracked UsageFactKind integer that becomes billable only while this row is effective.
    member val FactKind = 0 with get, set

    /// Stores the internal billable usage-kind integer selected by pricing rates.
    member val BillableUsageKind = 0 with get, set

    /// Stores the internal display label used by Operations tools.
    member val DisplayName = String.Empty with get, set

    /// Stores when the tracked fact kind first maps to the billable usage kind.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the mapping window, or null for open-ended mappings.
    member val EffectiveToUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the billable mapping row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Represents one effective-dated rate for a pricing plan and billable usage kind.
[<AllowNullLiteral>]
type PricingRateEntity() =

    /// Stores the durable pricing rate identity selected by later preview and ledger slices.
    member val PricingRateId = Guid.Empty with get, set

    /// Stores the pricing plan whose customers can receive this rate.
    member val PricingPlanId = Guid.Empty with get, set

    /// Stores the internal billable usage-kind integer priced by this rate.
    member val BillableUsageKind = 0 with get, set

    /// Stores the customer billing currency for this rate without provider cost or margin data.
    member val CurrencyCode = String.Empty with get, set

    /// Stores the rate unit label, such as byte-minute.
    member val UnitName = String.Empty with get, set

    /// Stores the measured quantity represented by one billable unit.
    member val UnitQuantity = 0L with get, set

    /// Stores the customer price in micros of the configured currency per unit quantity.
    member val UnitPriceMicros = 0L with get, set

    /// Stores when the rate can first be selected.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the rate window, or null for open-ended rates.
    member val EffectiveToUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the pricing rate row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

    /// References the pricing plan for EF schema relationship generation.
    member val PricingPlan: PricingPlanEntity = null with get, set

/// Represents one effective-dated assignment of a customer and repository scope to a pricing plan.
[<AllowNullLiteral>]
type CustomerPricingAssignmentEntity() =

    /// Stores the durable assignment identity selected by later preview and ledger slices.
    member val CustomerPricingAssignmentId = Guid.Empty with get, set

    /// Stores the Operations customer identity receiving the assigned pricing plan.
    member val CustomerId = Guid.Empty with get, set

    /// Stores the Grace owner scope for the customer assignment.
    member val OwnerId = Guid.Empty with get, set

    /// Stores the Grace organization scope for the customer assignment.
    member val OrganizationId = Guid.Empty with get, set

    /// Stores the Grace repository scope for the customer assignment.
    member val RepositoryId = Guid.Empty with get, set

    /// Stores the pricing plan selected for this customer and repository scope.
    member val PricingPlanId = Guid.Empty with get, set

    /// Stores when the assignment can first be selected.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the assignment window, or null for open-ended assignments.
    member val EffectiveToUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the customer assignment row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

    /// References the pricing plan for EF schema relationship generation.
    member val PricingPlan: PricingPlanEntity = null with get, set

/// Represents one rebuildable provisional charge-preview line for a complete pricing applicability segment.
[<AllowNullLiteral>]
type ChargePreviewLineEntity() =

    /// Stores the deterministic identity derived from the complete preview-line grain.
    member val ChargePreviewLineId = Guid.Empty with get, set

    /// Stores the customer whose provisional charge is represented.
    member val CustomerId = Guid.Empty with get, set

    /// Stores the Grace owner scope for the charged repository.
    member val OwnerId = Guid.Empty with get, set

    /// Stores the Grace organization scope for the charged repository.
    member val OrganizationId = Guid.Empty with get, set

    /// Stores the Grace repository scope for the charged usage.
    member val RepositoryId = Guid.Empty with get, set

    /// Stores the inclusive UTC start of the explicitly rebuilt preview period.
    member val PeriodFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive UTC end of the explicitly rebuilt preview period.
    member val PeriodToUtc = DateTime.MinValue with get, set

    /// Stores the tracked usage-fact kind contributing to this line.
    member val FactKind = 0 with get, set

    /// Stores the complete billable usage-kind mapping identity selected for the source facts.
    member val BillableUsageKindMappingId = Guid.Empty with get, set

    /// Stores the billable usage kind selected by the mapping.
    member val BillableUsageKind = 0 with get, set

    /// Stores the complete customer pricing assignment identity selected for the source facts.
    member val CustomerPricingAssignmentId = Guid.Empty with get, set

    /// Stores the complete pricing plan identity selected for the source facts.
    member val PricingPlanId = Guid.Empty with get, set

    /// Stores the complete pricing rate identity selected for the source facts.
    member val PricingRateId = Guid.Empty with get, set

    /// Stores the line currency without permitting cross-currency aggregation.
    member val CurrencyCode = String.Empty with get, set

    /// Stores the measured unit label carried by the selected rate.
    member val UnitName = String.Empty with get, set

    /// Stores the measured quantity represented by one priced unit.
    member val UnitQuantity = 0L with get, set

    /// Stores the selected customer price in whole currency micros per unit quantity.
    member val UnitPriceMicros = 0L with get, set

    /// Stores the inclusive start of the complete pricing applicability segment intersected with the preview period.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the complete pricing applicability segment intersected with the preview period.
    member val EffectiveToUtc = DateTime.MinValue with get, set

    /// Stores source quantity summed before line-charge calculation.
    member val TotalQuantity = 0L with get, set

    /// Stores the once-rounded provisional charge in whole currency micros.
    member val ChargeMicros = 0L with get, set

/// Represents one UTC calendar-month billing lifecycle for a customer repository assignment.
[<AllowNullLiteral>]
type BillingPeriodEntity() =
    /// Stores the deterministic period identity derived from its complete scope and month.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores the customer billed by this period.
    member val CustomerId = Guid.Empty with get, set
    /// Stores the Grace owner scope.
    member val OwnerId = Guid.Empty with get, set
    /// Stores the Grace organization scope.
    member val OrganizationId = Guid.Empty with get, set
    /// Stores the Grace repository scope.
    member val RepositoryId = Guid.Empty with get, set
    /// Stores the inclusive UTC month boundary.
    member val PeriodFromUtc = DateTime.MinValue with get, set
    /// Stores the exclusive UTC month boundary.
    member val PeriodToUtc = DateTime.MinValue with get, set
    /// Stores the Open, Provisional, Closed, or Corrected state.
    member val State = 0 with get, set
    /// Stores the current bounded close blocker classification.
    member val CloseBlockedCode: string = null with get, set
    /// Stores the current bounded redacted close blocker detail.
    member val CloseBlockedDetail: string = null with get, set
    /// Stores when close was last attempted.
    member val LastCloseAttemptAtUtc = Nullable<DateTime>() with get, set
    /// Stores consecutive close failures since the latest success.
    member val ConsecutiveCloseFailureCount = 0 with get, set
    /// Stores when the period first committed Closed state.
    member val ClosedAtUtc = Nullable<DateTime>() with get, set
    /// Stores the principal for a successful operator-initiated close retry.
    member val CloseInitiatedByPrincipalId: string = null with get, set
    /// Stores the reason code for a successful operator-initiated close retry.
    member val CloseReasonCode: string = null with get, set
    /// Stores the reason text for a successful operator-initiated close retry.
    member val CloseReasonText: string = null with get, set
    /// Stores the correlation identity for a successful operator-initiated close retry.
    member val CloseCorrelationId: string = null with get, set
    /// Stores the SQL-created UTC timestamp.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Stores durable source and pricing versions proving a saved preview is reusable.
[<AllowNullLiteral>]
type ChargePreviewFreshnessEntity() =
    /// Stores the billing period whose preview evidence was committed.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores a deterministic digest of every accepted fact identity and charging field.
    member val AcceptedFactsDigest = String.Empty with get, set
    /// Stores a deterministic digest of every applicable assignment, plan, mapping, and rate row.
    member val PricingDigest = String.Empty with get, set
    /// Stores when the preview and both digests committed together.
    member val PreviewCommittedAtUtc = DateTime.MinValue with get, set

/// Represents one append-only posted customer charge or correction.
[<AllowNullLiteral>]
type ChargeLedgerEntryEntity() =
    /// Stores the deterministic immutable entry identity.
    member val ChargeLedgerEntryId = Guid.Empty with get, set
    /// Stores the owning billing period.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores the Charge, Adjustment, or Reversal kind.
    member val EntryKind = 0 with get, set
    /// Links an initial charge to its final preview line.
    member val SourceChargePreviewLineId = Nullable<Guid>() with get, set
    /// Links a correction to the prior immutable entry when one exists.
    member val PriorChargeLedgerEntryId = Nullable<Guid>() with get, set
    /// Stores the automatic correction work identity so same-correlation late facts remain independently idempotent.
    member val BillingCorrectionWorkId = Nullable<Guid>() with get, set
    /// Stores the tracked usage-fact kind.
    member val FactKind = 0 with get, set
    /// Stores the complete billable mapping identity.
    member val BillableUsageKindMappingId = Guid.Empty with get, set
    /// Stores the billable usage kind.
    member val BillableUsageKind = 0 with get, set
    /// Stores the complete customer assignment identity.
    member val CustomerPricingAssignmentId = Guid.Empty with get, set
    /// Stores the complete pricing plan identity.
    member val PricingPlanId = Guid.Empty with get, set
    /// Stores the complete pricing rate identity.
    member val PricingRateId = Guid.Empty with get, set
    /// Stores the line currency.
    member val CurrencyCode = String.Empty with get, set
    /// Stores the pricing unit label.
    member val UnitName = String.Empty with get, set
    /// Stores the measured quantity represented by one priced unit.
    member val UnitQuantity = 0L with get, set
    /// Stores the historical unit price in whole currency micros.
    member val UnitPriceMicros = 0L with get, set
    /// Stores the inclusive pricing applicability boundary.
    member val EffectiveFromUtc = DateTime.MinValue with get, set
    /// Stores the exclusive pricing applicability boundary.
    member val EffectiveToUtc = DateTime.MinValue with get, set
    /// Stores the signed quantity posted by this entry.
    member val Quantity = 0L with get, set
    /// Stores the signed whole-micro charge posted by this entry.
    member val ChargeMicros = 0L with get, set
    /// Stores the initiating principal or Operations service identity.
    member val InitiatedByPrincipalId = String.Empty with get, set
    /// Stores the stable correction or close reason code.
    member val ReasonCode = String.Empty with get, set
    /// Stores human-readable correction provenance.
    member val ReasonText = String.Empty with get, set
    /// Stores the source workflow correlation identity.
    member val CorrelationId = String.Empty with get, set
    /// Stores when the immutable entry committed.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Represents one durable unresolved billing-relevant ingestion failure.
[<AllowNullLiteral>]
type BillingIngestionFailureEntity() =
    /// Stores a deterministic failure identity that prevents duplicate active evidence.
    member val BillingIngestionFailureId = Guid.Empty with get, set
    /// Stores the fact identity when the malformed message supplied one.
    member val UsageFactId = Nullable<Guid>() with get, set
    /// Stores the customer scope when known.
    member val CustomerId = Nullable<Guid>() with get, set
    /// Stores the owner scope when known.
    member val OwnerId = Nullable<Guid>() with get, set
    /// Stores the organization scope when known.
    member val OrganizationId = Nullable<Guid>() with get, set
    /// Stores the repository scope when known.
    member val RepositoryId = Nullable<Guid>() with get, set
    /// Stores the observation time when known.
    member val ObservedAtUtc = Nullable<DateTime>() with get, set
    /// Stores the redacted failure classification.
    member val FailureCode = String.Empty with get, set
    /// Stores bounded redacted diagnostic detail.
    member val FailureDetail = String.Empty with get, set
    /// Stores when this evidence was recorded.
    member val CreatedAtUtc = DateTime.MinValue with get, set
    /// Stores when exact acceptance or operator repair resolved this evidence.
    member val ResolvedAtUtc = Nullable<DateTime>() with get, set
    /// Stores repair provenance when explicitly resolved by an operator.
    member val ResolvedByPrincipalId: string = null with get, set
    /// Stores repair provenance when explicitly resolved by an operator.
    member val ResolutionReasonCode: string = null with get, set
    /// Stores repair provenance when explicitly resolved by an operator.
    member val ResolutionReasonText: string = null with get, set
    /// Stores repair provenance when explicitly resolved by an operator.
    member val ResolutionCorrelationId: string = null with get, set

/// Represents idempotent correction delivery created atomically with an accepted late fact.
[<AllowNullLiteral>]
type BillingCorrectionWorkEntity() =
    /// Stores the deterministic correction work identity.
    member val BillingCorrectionWorkId = Guid.Empty with get, set
    /// Stores the affected closed or corrected period.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores the accepted late fact that requires recalculation.
    member val UsageFactId = Guid.Empty with get, set
    /// Stores the source correlation propagated from ingestion.
    member val CorrelationId = String.Empty with get, set
    /// Stores when correction delivery was created.
    member val CreatedAtUtc = DateTime.MinValue with get, set
    /// Stores when correction processing completed.
    member val CompletedAtUtc = Nullable<DateTime>() with get, set
