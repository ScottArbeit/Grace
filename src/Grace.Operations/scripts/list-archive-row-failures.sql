-- Lists row-scoped Operations archive failures for Grace Operator repair or disposal.
-- Run against the Grace Operations SQL database after archive readiness reports row-scoped failures.

SET NOCOUNT ON;

SELECT
    UsageFactId,
    CorrelationId,
    FactKind,
    OwnerId,
    OrganizationId,
    RepositoryId,
    StoragePoolId,
    Quantity,
    ObservedAtUtc,
    ArchiveState,
    ArchiveBlobName,
    ArchiveChecksumSha256Hex,
    ArchiveByteLength,
    LastArchiveFailureAtUtc,
    LastArchiveFailureReason,
    ArchiveFailureCount,
    ArchiveRetiredAtUtc
FROM ops.RawUsageFact WITH (READCOMMITTEDLOCK)
WHERE
    LastArchiveFailureAtUtc IS NOT NULL
    OR ArchiveFailureCount > 0
    OR ArchiveRetiredAtUtc IS NOT NULL
ORDER BY
    CASE WHEN ArchiveRetiredAtUtc IS NULL THEN 0 ELSE 1 END,
    LastArchiveFailureAtUtc DESC,
    ObservedAtUtc ASC,
    UsageFactId ASC;
