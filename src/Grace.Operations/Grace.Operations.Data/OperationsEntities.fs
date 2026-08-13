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

/// Represents durable rejection evidence that blocks completeness only when every scope field is present.
[<AllowNullLiteral>]
type UsageFactRejectionEntity() =

    /// Stores the stable rejection identity used for operator repair.
    member val RejectionId = Guid.Empty with get, set

    /// Stores the rejected fact identity when it was supplied.
    member val UsageFactId = Nullable<Guid>() with get, set

    /// Stores the reported owner without inferring a scope when other fields are absent.
    member val OwnerId = Nullable<Guid>() with get, set

    /// Stores the reported organization without inferring a scope when other fields are absent.
    member val OrganizationId = Nullable<Guid>() with get, set

    /// Stores the reported repository without inferring a scope when other fields are absent.
    member val RepositoryId = Nullable<Guid>() with get, set

    /// Stores the UTC month only when an observation timestamp was actually supplied.
    member val MonthStartUtc = Nullable<DateTime>() with get, set

    /// Stores the bounded operator-visible rejection reason.
    member val Reason = String.Empty with get, set

    /// States whether this rejection blocks its exact complete scope.
    member val IsActive = false with get, set

    /// Stores when accepted usage repaired a scoped blocker.
    member val ResolvedAtUtc = Nullable<DateTime>() with get, set

    /// Stores SQL-created ordering evidence for diagnostics.
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Represents the immutable SQL source of truth for a supported fact before any Service Bus signal is sent.
[<AllowNullLiteral>]
type UsageFactJournalEntity() =

    /// Stores the stable fact identity shared by journal, broker message, raw usage, and rejection evidence.
    member val UsageFactId = Guid.Empty with get, set

    /// Stores the canonical immutable fact bytes compared on append and processing replay.
    member val RawPayload: byte array = Array.empty with get, set

    /// Stores the correlation lineage without using it as a journal identity.
    member val CorrelationId = String.Empty with get, set

    /// Stores the persisted usage-fact kind.
    member val FactKind = 0 with get, set

    /// Stores the complete billing scope and resource identity carried by the immutable fact.
    member val OwnerId = Guid.Empty with get, set
    member val OrganizationId = Guid.Empty with get, set
    member val RepositoryId = Guid.Empty with get, set
    member val StoragePoolId = String.Empty with get, set
    member val Quantity = 0L with get, set
    member val ObservedAtUtc = DateTime.MinValue with get, set

    /// Stores Pending, Accepted, or Rejected; sending and settlement never change this state.
    member val State = 0 with get, set

    /// Stores when the immutable journal row became durable.
    member val CreatedAtUtc = DateTime.MinValue with get, set

    /// Stores when a terminal state was durably recorded.
    member val TerminalAtUtc = Nullable<DateTime>() with get, set

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

/// Represents one exact repository UTC-month that may later receive one immutable close result.
[<AllowNullLiteral>]
type BillingPeriodEntity() =

    /// Stores the deterministic identity for the complete billing scope and month.
    member val BillingPeriodId = Guid.Empty with get, set
    member val OwnerId = Guid.Empty with get, set
    member val OrganizationId = Guid.Empty with get, set
    member val RepositoryId = Guid.Empty with get, set
    member val MonthStartUtc = DateTime.MinValue with get, set
    member val NextMonthStartUtc = DateTime.MinValue with get, set
    member val State = 0 with get, set
    member val RetryDiagnostic: string = null with get, set
    member val RetryDiagnosticAtUtc = Nullable<DateTime>() with get, set
    member val CreatedAtUtc = DateTime.MinValue with get, set
    member val UpdatedAtUtc = DateTime.MinValue with get, set

/// Represents one append-only initial posting copied from an already calculated preview line.
[<AllowNullLiteral>]
type ChargeEntity() =

    /// Stores the deterministic posting identity.
    member val ChargeId = Guid.Empty with get, set

    /// Stores the exact billing scope selected by the immutable preview line.
    member val OwnerId = Guid.Empty with get, set
    member val OrganizationId = Guid.Empty with get, set
    member val RepositoryId = Guid.Empty with get, set
    member val BillingPeriodId = Guid.Empty with get, set
    member val ChargePreviewLineId = Guid.Empty with get, set

    /// Stores the exact preview interval and usage-kind provenance copied at posting time.
    member val PeriodFromUtc = DateTime.MinValue with get, set
    member val PeriodToUtc = DateTime.MinValue with get, set
    member val FactKind = 0 with get, set
    member val BillableUsageKindMappingId = Guid.Empty with get, set
    member val BillableUsageKind = 0 with get, set
    member val PricingAssignmentId = Guid.Empty with get, set
    member val PricingPlanId = Guid.Empty with get, set
    member val PricingRateId = Guid.Empty with get, set
    member val CurrencyCode = String.Empty with get, set

    /// Stores the immutable priced-unit and effective-rate provenance copied from the preview line.
    member val UnitName = String.Empty with get, set
    member val UnitQuantity = 0L with get, set
    member val UnitPriceMicros = 0L with get, set
    member val EffectiveFromUtc = DateTime.MinValue with get, set
    member val EffectiveToUtc = DateTime.MinValue with get, set
    member val TotalQuantity = 0L with get, set
    member val ChargeMicros = 0L with get, set
    member val CreatedAtUtc = DateTime.MinValue with get, set

/// Preserves the immutable accepted-fact and pricing evidence for one closed period.
[<AllowNullLiteral>]
type BillingPeriodCloseEvidenceEntity() =

    /// Uses the billing period identity as the evidence identity.
    member val BillingPeriodId = Guid.Empty with get, set
    member val AcceptedFactDigestSha256Hex = String.Empty with get, set
    member val PricingPreviewDigestSha256Hex = String.Empty with get, set
    member val ClosedAtUtc = DateTime.MinValue with get, set
    member val ScheduledOperationProvenance = String.Empty with get, set

/// Represents one Pending post-close handoff without copying accepted usage payload.
[<AllowNullLiteral>]
type BillingPeriodLateWorkEntity() =

    /// Identifies the already closed period that owns the future correction decision.
    member val BillingPeriodId = Guid.Empty with get, set

    /// Identifies the newly accepted raw fact.
    member val UsageFactId = Guid.Empty with get, set

    /// Stores the only supported handoff state: Pending.
    member val State = 0 with get, set

    /// Stores the database-assigned creation time for this handoff.
    member val CreatedAtUtc = DateTime.MinValue with get, set
