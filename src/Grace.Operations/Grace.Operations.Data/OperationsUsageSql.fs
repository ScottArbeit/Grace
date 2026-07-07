namespace Grace.Operations.Data

/// Provides SQL Server schema names and command text for the operations usage fact tables.
[<RequireQualifiedAccess>]
module OperationsUsageSql =

    /// Names the SQL schema used by Grace operations data.
    [<Literal>]
    let SchemaName = "ops"

    /// Names the raw immutable fact table without schema qualification for EF migrations.
    [<Literal>]
    let RawUsageFactTableName = "RawUsageFact"

    /// Names the raw immutable fact table.
    [<Literal>]
    let RawUsageFactTable = "ops.RawUsageFact"

    /// Names the minute aggregate table without schema qualification for EF migrations.
    [<Literal>]
    let UsageAggregateMinuteTableName = "UsageAggregateMinute"

    /// Names the minute aggregate table derived from accepted raw facts.
    [<Literal>]
    let UsageAggregateMinuteTable = "ops.UsageAggregateMinute"

    /// Keeps storage-pool identities case-sensitive even when the database default collation is case-insensitive.
    [<Literal>]
    let CaseSensitiveStoragePoolIdCollation = "Latin1_General_100_BIN2"

    /// Limits correlation identifiers to the raw fact column width used by the operations store.
    [<Literal>]
    let CorrelationIdMaxLength = 200

    /// Limits storage-pool identifiers to the aggregate key column width used by the operations store.
    [<Literal>]
    let StoragePoolIdMaxLength = 256

    /// Limits deterministic archive Blob names stored with raw facts.
    [<Literal>]
    let ArchiveBlobNameMaxLength = 512

    /// Fixes the length of lowercase SHA-256 hexadecimal checksums stored with archive pointers.
    [<Literal>]
    let ArchiveChecksumSha256HexLength = 64

    /// Creates the configured operations database when SQL Server does not already contain it.
    [<Literal>]
    let CreateDatabaseIfMissing =
        """
IF DB_ID(@DatabaseName) IS NULL
BEGIN
    DECLARE @CreateDatabaseSql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
    EXEC(@CreateDatabaseSql);
END;
"""

    /// Creates the operations schema before EF Core touches the schema-scoped migration history table.
    [<Literal>]
    let CreateSchemaIfMissing =
        """
IF SCHEMA_ID(N'ops') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [ops]');
END;
"""

    /// Inserts one raw usage fact only when its durable identity has not already been accepted.
    [<Literal>]
    let TryInsertRawUsageFact =
        """
INSERT INTO ops.RawUsageFact
(
    UsageFactId,
    RawPayload,
    CorrelationId,
    FactKind,
    OwnerId,
    OrganizationId,
    RepositoryId,
    StoragePoolId,
    Quantity,
    ObservedAtUtc
)
SELECT
    @UsageFactId,
    @RawPayload,
    @CorrelationId,
    @FactKind,
    @OwnerId,
    @OrganizationId,
    @RepositoryId,
    @StoragePoolId,
    @Quantity,
    @ObservedAtUtc
WHERE NOT EXISTS
(
    SELECT 1
    FROM ops.RawUsageFact WITH (UPDLOCK, HOLDLOCK)
    WHERE UsageFactId = @UsageFactId
);
"""

    /// Selects hot facts and partially verified facts that need archive processing or cleanup.
    [<Literal>]
    let SelectRawUsageFactsForArchive =
        """
SELECT TOP (@BatchSize)
    UsageFactId,
    RawPayload,
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
    ArchiveByteLength
FROM ops.RawUsageFact WITH (READPAST)
WHERE ObservedAtUtc < @ObservedBeforeUtc
AND
(
    (
        ArchiveState = @ArchiveStateHot
        AND RawPayload IS NOT NULL
        AND DATALENGTH(RawPayload) > 0
    )
    OR ArchiveState = @ArchiveStateVerified
)
ORDER BY ObservedAtUtc ASC, UsageFactId ASC;
"""

    /// Records verified Blob authority while retaining the hot payload for an idempotent cleanup retry.
    [<Literal>]
    let MarkRawUsageFactArchiveVerified =
        """
UPDATE ops.RawUsageFact
SET
    ArchiveState = @ArchiveStateVerified,
    ArchiveBlobName = @ArchiveBlobName,
    ArchiveChecksumSha256Hex = @ArchiveChecksumSha256Hex,
    ArchiveByteLength = @ArchiveByteLength,
    ArchiveVerifiedAtUtc = SYSUTCDATETIME()
WHERE UsageFactId = @UsageFactId
AND ArchiveState = @ArchiveStateHot
AND RawPayload IS NOT NULL
AND DATALENGTH(RawPayload) > 0;

IF @@ROWCOUNT = 1
BEGIN
    SELECT 1;
END
ELSE IF EXISTS
(
    SELECT 1
    FROM ops.RawUsageFact
    WHERE UsageFactId = @UsageFactId
    AND ArchiveState IN (@ArchiveStateVerified, @ArchiveStateArchived)
    AND ArchiveBlobName = @ArchiveBlobName
    AND ArchiveChecksumSha256Hex = @ArchiveChecksumSha256Hex
    AND ArchiveByteLength = @ArchiveByteLength
)
BEGIN
    SELECT 0;
END
ELSE
BEGIN
    THROW 57201, 'Raw UsageFact archive verification could not be recorded because SQL archive state no longer matches the verified Blob pointer.', 1;
END;
"""

    /// Clears the hot payload only after SQL already carries the exact verified Blob authority.
    [<Literal>]
    let CompleteRawUsageFactArchive =
        """
UPDATE ops.RawUsageFact
SET
    RawPayload = NULL,
    ArchiveState = @ArchiveStateArchived,
    ArchivedAtUtc = SYSUTCDATETIME()
WHERE UsageFactId = @UsageFactId
AND ArchiveState = @ArchiveStateVerified
AND ArchiveBlobName = @ArchiveBlobName
AND ArchiveChecksumSha256Hex = @ArchiveChecksumSha256Hex
AND ArchiveByteLength = @ArchiveByteLength;

IF @@ROWCOUNT = 1
BEGIN
    SELECT 1;
END
ELSE IF EXISTS
(
    SELECT 1
    FROM ops.RawUsageFact
    WHERE UsageFactId = @UsageFactId
    AND ArchiveState = @ArchiveStateArchived
    AND RawPayload IS NULL
    AND ArchiveBlobName = @ArchiveBlobName
    AND ArchiveChecksumSha256Hex = @ArchiveChecksumSha256Hex
    AND ArchiveByteLength = @ArchiveByteLength
)
BEGIN
    SELECT 0;
END
ELSE
BEGIN
    THROW 57202, 'Raw UsageFact hot payload could not be cleared because verified SQL archive authority is missing or different.', 1;
END;
"""

    /// Adds a quantity to the minute aggregate row associated with a newly accepted raw fact.
    [<Literal>]
    let AddToUsageAggregateMinute =
        """
MERGE ops.UsageAggregateMinute WITH (HOLDLOCK) AS target
USING
(
    SELECT
        @FactKind AS FactKind,
        @OwnerId AS OwnerId,
        @OrganizationId AS OrganizationId,
        @RepositoryId AS RepositoryId,
        CAST(@StoragePoolId AS nvarchar(256)) COLLATE Latin1_General_100_BIN2 AS StoragePoolId,
        @BucketStartUtc AS BucketStartUtc,
        @Quantity AS Quantity
) AS source
ON
    target.FactKind = source.FactKind
    AND target.OwnerId = source.OwnerId
    AND target.OrganizationId = source.OrganizationId
    AND target.RepositoryId = source.RepositoryId
    AND target.StoragePoolId = source.StoragePoolId
    AND target.BucketStartUtc = source.BucketStartUtc
WHEN MATCHED THEN
    UPDATE SET
        Quantity = target.Quantity + source.Quantity,
        UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT
    (
        FactKind,
        OwnerId,
        OrganizationId,
        RepositoryId,
        StoragePoolId,
        BucketStartUtc,
        Quantity
    )
    VALUES
    (
        source.FactKind,
        source.OwnerId,
        source.OrganizationId,
        source.RepositoryId,
        source.StoragePoolId,
        source.BucketStartUtc,
        source.Quantity
    );
"""
