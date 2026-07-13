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

    /// Stores the database-assigned UTC acceptance instant used to order late-fact correction routing against period close.
    member val AcceptedAtUtc = DateTime.MinValue with get, set

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

/// Represents one effective-dated owner pricing plan.
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

    /// Stores the pricing plan whose owners can receive this rate.
    member val PricingPlanId = Guid.Empty with get, set

    /// Stores the internal billable usage-kind integer priced by this rate.
    member val BillableUsageKind = 0 with get, set

    /// Stores the owner billing currency for this rate without provider cost or margin data.
    member val CurrencyCode = String.Empty with get, set

    /// Stores the rate unit label, such as byte-minute.
    member val UnitName = String.Empty with get, set

    /// Stores the measured quantity represented by one billable unit.
    member val UnitQuantity = 0L with get, set

    /// Stores the owner price in micros of the configured currency per unit quantity.
    member val UnitPriceMicros = 0L with get, set

    /// Stores when the rate can first be selected.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the rate window, or null for open-ended rates.
    member val EffectiveToUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the pricing rate row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

    /// References the pricing plan for EF schema relationship generation.
    member val PricingPlan: PricingPlanEntity = null with get, set

/// Represents one effective-dated assignment of a owner and repository scope to a pricing plan.
[<AllowNullLiteral>]
type PricingAssignmentEntity() =

    /// Stores the durable assignment identity selected by later preview and ledger slices.
    member val PricingAssignmentId = Guid.Empty with get, set

    /// Stores the Operations owner identity receiving the assigned pricing plan.
    /// Stores the Grace owner scope for the owner assignment.
    member val OwnerId = Guid.Empty with get, set

    /// Stores the Grace organization scope for the owner assignment.
    member val OrganizationId = Guid.Empty with get, set

    /// Stores the Grace repository scope for the owner assignment.
    member val RepositoryId = Guid.Empty with get, set

    /// Stores the pricing plan selected for this owner and repository scope.
    member val PricingPlanId = Guid.Empty with get, set

    /// Stores when the assignment can first be selected.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the assignment window, or null for open-ended assignments.
    member val EffectiveToUtc = Nullable<DateTime>() with get, set

    /// Stores the SQL-created UTC timestamp for the owner assignment row.
    member val CreatedAtUtc = DateTime.MinValue with get, set

    /// References the pricing plan for EF schema relationship generation.
    member val PricingPlan: PricingPlanEntity = null with get, set

/// Represents one rebuildable provisional charge-preview line for a complete pricing applicability segment.
[<AllowNullLiteral>]
type ChargePreviewLineEntity() =

    /// Stores the deterministic identity derived from the complete preview-line grain.
    member val ChargePreviewLineId = Guid.Empty with get, set

    /// Stores the owner whose provisional charge is represented.
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

    /// Stores the complete owner pricing assignment identity selected for the source facts.
    member val PricingAssignmentId = Guid.Empty with get, set

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

    /// Stores the selected owner price in whole currency micros per unit quantity.
    member val UnitPriceMicros = 0L with get, set

    /// Stores the inclusive start of the complete pricing applicability segment intersected with the preview period.
    member val EffectiveFromUtc = DateTime.MinValue with get, set

    /// Stores the exclusive end of the complete pricing applicability segment intersected with the preview period.
    member val EffectiveToUtc = DateTime.MinValue with get, set

    /// Stores source quantity summed before line-charge calculation.
    member val TotalQuantity = 0L with get, set

    /// Stores the once-rounded provisional charge in whole currency micros.
    member val ChargeMicros = 0L with get, set

/// Represents one owner-scoped UTC billing month and its bounded close state.
[<AllowNullLiteral>]
type BillingPeriodEntity() =
    /// Stores the deterministic owner scope and month identity.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores the billed Grace owner.
    member val OwnerId = Guid.Empty with get, set
    /// Stores the billed Grace organization.
    member val OrganizationId = Guid.Empty with get, set
    /// Stores the billed Grace repository.
    member val RepositoryId = Guid.Empty with get, set
    /// Stores the inclusive UTC calendar-month boundary.
    member val PeriodFromUtc = DateTime.MinValue with get, set
    /// Stores the exclusive UTC calendar-month boundary.
    member val PeriodToUtc = DateTime.MinValue with get, set
    /// Stores the durable lifecycle state.
    member val State = 0 with get, set
    /// Stores the current bounded repair blocker code.
    member val CloseBlockedCode: string = null with get, set
    /// Stores bounded redacted operator diagnostics for the current blocker.
    member val CloseBlockedDetail: string = null with get, set
    /// Stores the most recent close attempt without retaining a close-attempt history.
    member val LastCloseAttemptAtUtc = Nullable<DateTime>() with get, set
    /// Stores consecutive failures for current diagnostics only.
    member val ConsecutiveCloseFailureCount = 0 with get, set
    /// Stores when immutable initial charges committed.
    member val ClosedAtUtc = Nullable<DateTime>() with get, set
    /// Stores close provenance for an operator retry.
    member val CloseInitiatedByPrincipalId: string = null with get, set
    /// Stores close reason classification.
    member val CloseReasonCode: string = null with get, set
    /// Stores bounded close reason detail.
    member val CloseReasonText: string = null with get, set
    /// Stores the accepted correlation identity for the close.
    member val CloseCorrelationId: string = null with get, set
    /// Stores the stable code for a deterministic calculation failure that cannot be retried on this period.
    member val PermanentFailureCode: string = null with get, set
    /// Stores bounded operator-visible detail for a deterministic calculation failure.
    member val PermanentFailureDetail: string = null with get, set
    /// Stores when this period became permanently failed.
    member val PermanentlyFailedAtUtc = Nullable<DateTime>() with get, set
    /// Stores the principal that initiated the calculation attempt that permanently failed.
    member val PermanentFailureInitiatedByPrincipalId: string = null with get, set
    /// Stores the reason classification for the permanent calculation failure.
    member val PermanentFailureReasonCode: string = null with get, set
    /// Stores bounded reason detail for the permanent calculation failure.
    member val PermanentFailureReasonText: string = null with get, set
    /// Stores the correlation identity that produced the permanent calculation failure.
    member val PermanentFailureCorrelationId: string = null with get, set
    /// Stores the SQL creation time.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Stores source and pricing digests for the preview committed with a billing period.
[<AllowNullLiteral>]
type ChargePreviewFreshnessEntity() =
    /// Stores the related billing period identity.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores a deterministic accepted-fact digest.
    member val AcceptedFactsDigest = String.Empty with get, set
    /// Stores a deterministic applicable-pricing digest.
    member val PricingDigest = String.Empty with get, set
    /// Stores when the preview and digests committed together.
    member val PreviewCommittedAtUtc = DateTime.MinValue with get, set

/// Represents one immutable initial charge, automatic correction, or manual adjustment.
[<AllowNullLiteral>]
type ChargeLedgerEntryEntity() =
    /// Stores the immutable entry identity.
    member val ChargeLedgerEntryId = Guid.Empty with get, set
    /// Stores the owning billing period.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores charge, adjustment, or reversal kind.
    member val EntryKind = 0 with get, set
    /// Links initial charges to a final preview line.
    member val SourceChargePreviewLineId = Nullable<Guid>() with get, set
    /// Links a correction to its immediately preceding compatible entry when applicable.
    member val PriorChargeLedgerEntryId = Nullable<Guid>() with get, set
    /// Stores automatic correction work identity for exact retry isolation.
    member val BillingCorrectionWorkId = Nullable<Guid>() with get, set
    /// Stores the tracked usage kind.
    member val FactKind = 0 with get, set
    /// Stores the immutable mapping identity.
    member val BillableUsageKindMappingId = Guid.Empty with get, set
    /// Stores the selected billable usage kind.
    member val BillableUsageKind = 0 with get, set
    /// Stores the immutable owner-scoped pricing assignment identity.
    member val PricingAssignmentId = Guid.Empty with get, set
    /// Stores the plan selected for the immutable amount.
    member val PricingPlanId = Guid.Empty with get, set
    /// Stores the rate selected for the immutable amount.
    member val PricingRateId = Guid.Empty with get, set
    /// Stores the three-letter currency.
    member val CurrencyCode = String.Empty with get, set
    /// Stores the unit label.
    member val UnitName = String.Empty with get, set
    /// Stores quantity per rate unit.
    member val UnitQuantity = 0L with get, set
    /// Stores price per unit in whole micros.
    member val UnitPriceMicros = 0L with get, set
    /// Stores inclusive pricing applicability start.
    member val EffectiveFromUtc = DateTime.MinValue with get, set
    /// Stores exclusive pricing applicability end.
    member val EffectiveToUtc = DateTime.MinValue with get, set
    /// Stores signed posted source quantity.
    member val Quantity = 0L with get, set
    /// Stores signed posted amount in whole micros.
    member val ChargeMicros = 0L with get, set
    /// Stores complete mutation provenance.
    member val InitiatedByPrincipalId = String.Empty with get, set
    /// Stores the stable mutation reason code.
    member val ReasonCode = String.Empty with get, set
    /// Stores human-readable mutation reason.
    member val ReasonText = String.Empty with get, set
    /// Stores correlation propagated under the accepted 200-character contract.
    member val CorrelationId = String.Empty with get, set
    /// Stores SQL commit time.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Represents unresolved billing-relevant ingestion evidence scoped when the malformed fact supplied scope.
[<AllowNullLiteral>]
type BillingIngestionFailureEntity() =
    /// Stores deterministic evidence identity.
    member val BillingIngestionFailureId = Guid.Empty with get, set
    /// Stores the fact identity when present.
    member val UsageFactId = Nullable<Guid>() with get, set
    /// Stores nullable owner scope.
    member val OwnerId = Nullable<Guid>() with get, set
    /// Stores nullable organization scope.
    member val OrganizationId = Nullable<Guid>() with get, set
    /// Stores nullable repository scope.
    member val RepositoryId = Nullable<Guid>() with get, set
    /// Stores nullable observed time.
    member val ObservedAtUtc = Nullable<DateTime>() with get, set
    /// Stores the redacted failure code.
    member val FailureCode = String.Empty with get, set
    /// Stores bounded redacted failure detail.
    member val FailureDetail = String.Empty with get, set
    /// Stores correlation evidence.
    member val CorrelationId = String.Empty with get, set
    /// Stores failure creation time.
    member val CreatedAtUtc = DateTime.MinValue with get, set
    /// Stores repair resolution time.
    member val ResolvedAtUtc = Nullable<DateTime>() with get, set
    /// Stores repair provenance.
    member val ResolutionDetail: string = null with get, set

/// Represents durable, retry-isolated automatic work for one accepted late UsageFact.
[<AllowNullLiteral>]
type BillingCorrectionWorkEntity() =
    /// Stores deterministic period/fact work identity.
    member val BillingCorrectionWorkId = Guid.Empty with get, set
    /// Stores the closed or corrected period to repair.
    member val BillingPeriodId = Guid.Empty with get, set
    /// Stores the accepted late fact identity.
    member val UsageFactId = Guid.Empty with get, set
    /// Stores current bounded pending reason when pricing is unavailable.
    member val BlockedCode: string = null with get, set
    /// Stores current bounded pending detail.
    member val BlockedDetail: string = null with get, set
    /// Stores whether automatic polling may select this unfinished correction row.
    member val IsAutomaticRetryEligible = true with get, set
    /// Stores when a deterministic correction calculation failure permanently removed this exact work item from automatic processing.
    member val PermanentlyFailedAtUtc = Nullable<DateTime>() with get, set
    /// Stores the database UTC instant when an operator made this exact row eligible again.
    member val ReenabledAtUtc = Nullable<DateTime>() with get, set
    /// Stores the principal that explicitly re-enabled this correction row.
    member val ReenabledByPrincipalId: string = null with get, set
    /// Stores the operator's bounded re-enable reason code.
    member val ReenabledReasonCode: string = null with get, set
    /// Stores the operator's bounded re-enable reason text.
    member val ReenabledReasonText: string = null with get, set
    /// Stores the operator correlation that identifies the exact re-enable outcome.
    member val ReenabledCorrelationId: string = null with get, set
    /// Stores when processing committed successfully.
    member val CompletedAtUtc = Nullable<DateTime>() with get, set
    /// Stores SQL creation time.
    member val CreatedAtUtc = DateTime.MinValue with get, set
