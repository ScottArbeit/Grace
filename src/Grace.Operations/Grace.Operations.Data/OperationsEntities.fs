namespace Grace.Operations.Data

open System

/// Represents an immutable operational usage fact row managed by the Operations EF model.
[<AllowNullLiteral>]
type RawUsageFactEntity() =

    /// Stores the durable idempotency key supplied by the UsageFact contract.
    member val UsageFactId = Guid.Empty with get, set

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
