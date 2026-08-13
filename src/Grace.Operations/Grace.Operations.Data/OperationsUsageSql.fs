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

    /// Names operator-visible rejection evidence that blocks a scope only when its complete tuple is known.
    [<Literal>]
    let UsageFactRejectionTable = "ops.UsageFactRejection"

    /// Names the immutable pre-send fact journal used by dispatch and billing completeness.
    [<Literal>]
    let UsageFactJournalTable = "ops.UsageFactJournal"

    /// Names the journal table without schema qualification for EF migrations.
    [<Literal>]
    let UsageFactJournalTableName = "UsageFactJournal"

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

    /// Caps parameterized payload restore batches below SQL Server's 2100-parameter command limit.
    [<Literal>]
    let RehydrationPayloadBatchSize = 400

    /// Caps raw-payload archive retention batches so one worker pass cannot materialize too many hot rows.
    [<Literal>]
    let ArchiveRetentionBatchSizeMax = 400

    /// Caps repeated row-scoped archive failures before the worker retires the row for operator repair.
    [<Literal>]
    let ArchiveFailureRetirementThreshold = 5

    /// Limits the operator-visible archive failure summary retained with a raw fact row.
    [<Literal>]
    let ArchiveFailureReasonMaxLength = 400

    /// Acquires the sole transaction-owned application lock used by accepted usage, replay, rejection, and reads.
    [<Literal>]
    let AcquireBillingCompletenessScopeLock =
        """
DECLARE @LockResult int;
EXEC @LockResult = sys.sp_getapplock
    @Resource = @BillingCompletenessLockResource,
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = @BillingCompletenessLockTimeoutMilliseconds;
IF @LockResult < 0
    THROW 57220, 'Could not acquire the Operations billing completeness coordination lock.', 1;
"""

    /// Rejects a reused fact identity unless all committed accepted or active rejection evidence has the exact requested scope.
    [<Literal>]
    let EnsureUsageFactIdMatchesBillingCompletenessScope =
        """
IF EXISTS
(
    SELECT 1
    FROM ops.RawUsageFact WITH (UPDLOCK, HOLDLOCK)
    WHERE UsageFactId = @UsageFactId
      AND
      (
          OwnerId <> @OwnerId
          OR OrganizationId <> @OrganizationId
          OR RepositoryId <> @RepositoryId
          OR ObservedAtUtc < @MonthStartUtc
          OR ObservedAtUtc >= @NextMonthStartUtc
      )
)
OR EXISTS
(
    SELECT 1
    FROM ops.UsageFactRejection WITH (UPDLOCK, HOLDLOCK)
    WHERE UsageFactId = @UsageFactId
      AND IsActive = 1
      AND OwnerId IS NOT NULL
      AND OrganizationId IS NOT NULL
      AND RepositoryId IS NOT NULL
      AND MonthStartUtc IS NOT NULL
      AND
      (
          OwnerId <> @OwnerId
          OR OrganizationId <> @OrganizationId
          OR RepositoryId <> @RepositoryId
          OR MonthStartUtc <> @MonthStartUtc
      )
)
    THROW 57221, 'UsageFactId is already bound to a different billing completeness scope.', 1;
"""

    /// Records the first active scoped rejection unless accepted durable usage already owns the exact fact and scope.
    [<Literal>]
    let RecordScopedUsageFactRejection =
        """
IF NOT EXISTS (SELECT 1 FROM ops.RawUsageFact WITH (UPDLOCK, HOLDLOCK) WHERE UsageFactId = @UsageFactId
               AND OwnerId = @OwnerId AND OrganizationId = @OrganizationId AND RepositoryId = @RepositoryId
               AND ObservedAtUtc >= @MonthStartUtc AND ObservedAtUtc < @NextMonthStartUtc)
AND NOT EXISTS (SELECT 1 FROM ops.UsageFactRejection WITH (UPDLOCK, HOLDLOCK) WHERE UsageFactId = @UsageFactId
                AND OwnerId = @OwnerId AND OrganizationId = @OrganizationId AND RepositoryId = @RepositoryId
                AND MonthStartUtc = @MonthStartUtc AND IsActive = 1)
BEGIN
    INSERT INTO ops.UsageFactRejection (RejectionId, UsageFactId, OwnerId, OrganizationId, RepositoryId, MonthStartUtc, Reason, IsActive)
    VALUES (@RejectionId, @UsageFactId, @OwnerId, @OrganizationId, @RepositoryId, @MonthStartUtc, @Reason, 1);
END;

SELECT TOP (1) RejectionId, UsageFactId, OwnerId, OrganizationId, RepositoryId, MonthStartUtc, Reason, IsActive
FROM ops.UsageFactRejection
WHERE UsageFactId = @UsageFactId AND OwnerId = @OwnerId AND OrganizationId = @OrganizationId AND RepositoryId = @RepositoryId
  AND MonthStartUtc = @MonthStartUtc AND IsActive = 1
ORDER BY CreatedAtUtc ASC, RejectionId ASC;
"""

    /// Stores partial rejection evidence without attaching it to an invented scope.
    [<Literal>]
    let RecordUnscopedUsageFactRejection =
        """
INSERT INTO ops.UsageFactRejection (RejectionId, UsageFactId, OwnerId, OrganizationId, RepositoryId, MonthStartUtc, Reason, IsActive)
VALUES (@RejectionId, @UsageFactId, @OwnerId, @OrganizationId, @RepositoryId, @MonthStartUtc, @Reason, 1);
"""

    /// Resolves the exact scoped blocker inside the accepted usage transaction.
    [<Literal>]
    let ResolveScopedUsageFactRejection =
        """
UPDATE ops.UsageFactRejection SET IsActive = 0, ResolvedAtUtc = SYSUTCDATETIME()
WHERE UsageFactId = @UsageFactId AND OwnerId = @OwnerId AND OrganizationId = @OrganizationId AND RepositoryId = @RepositoryId
  AND MonthStartUtc = @MonthStartUtc AND IsActive = 1;
"""

    /// Reads whether an active blocker remains after the caller acquires the central scope lock.
    [<Literal>]
    let HasActiveScopedUsageFactRejection =
        """
SELECT CASE WHEN EXISTS (SELECT 1 FROM ops.UsageFactRejection WITH (UPDLOCK, HOLDLOCK)
                         WHERE OwnerId = @OwnerId AND OrganizationId = @OrganizationId AND RepositoryId = @RepositoryId
                           AND MonthStartUtc = @MonthStartUtc AND IsActive = 1)
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
"""

    /// Reads unresolved journal truth only after the caller owns the matching completeness lock.
    [<Literal>]
    let HasUnresolvedUsageFactJournal =
        """
SELECT CASE WHEN EXISTS (SELECT 1 FROM ops.UsageFactJournal WITH (UPDLOCK, HOLDLOCK)
                         WHERE OwnerId = @OwnerId AND OrganizationId = @OrganizationId AND RepositoryId = @RepositoryId
                           AND ObservedAtUtc >= @MonthStartUtc AND ObservedAtUtc < @NextMonthStartUtc
                           AND State IN (@PendingState, @RejectedState))
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
"""

    /// Caps expired temporary-hot cleanup statements to a practical SQL Server row batch.
    [<Literal>]
    let TemporaryHotCleanupBatchSize = 1000

    /// Names the filtered expiry index used by periodic temporary-hot cleanup.
    [<Literal>]
    let TemporaryHotCleanupExpiryIndexName = "IX_ops_RawUsageFact_RehydrationExpiresAtUtc"

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

    /// Stages the minimal Pending late-work handoff only for a newly accepted fact in the exact closed billing period.
    [<Literal>]
    let RecordClosedPeriodLateWork =
        """
INSERT INTO ops.BillingPeriodLateWork (BillingPeriodId, UsageFactId, State)
SELECT period.BillingPeriodId, @UsageFactId, 0
FROM ops.BillingPeriod AS period WITH (UPDLOCK, HOLDLOCK)
WHERE period.OwnerId = @OwnerId
  AND period.OrganizationId = @OrganizationId
  AND period.RepositoryId = @RepositoryId
  AND period.MonthStartUtc = @MonthStartUtc
  AND period.NextMonthStartUtc = @NextMonthStartUtc
  AND period.State = 2
  AND NOT EXISTS
  (
      SELECT 1
      FROM ops.BillingPeriodLateWork AS existing
      WHERE existing.BillingPeriodId = period.BillingPeriodId
        AND existing.UsageFactId = @UsageFactId
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
    ArchiveByteLength,
    LastArchiveFailureReason,
    LastArchiveFailureAtUtc,
    ArchiveFailureCount,
    ArchiveRetiredAtUtc
FROM ops.RawUsageFact WITH (READPAST, READCOMMITTEDLOCK)
WHERE ObservedAtUtc < @ObservedBeforeUtc
AND ArchiveRetiredAtUtc IS NULL
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

    /// Selects archived usage fact rows whose compact SQL index can authorize Blob replay.
    [<Literal>]
    let SelectArchivedRawUsageFactsForReplay =
        """
SELECT TOP (@BatchSize)
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
    ArchiveByteLength
FROM ops.RawUsageFact WITH (READCOMMITTEDLOCK)
WHERE ArchiveState = @ArchiveStateArchived
AND ArchiveBlobName IS NOT NULL
AND ArchiveChecksumSha256Hex IS NOT NULL
AND ArchiveByteLength IS NOT NULL
AND (@OwnerId IS NULL OR OwnerId = @OwnerId)
AND (@OrganizationId IS NULL OR OrganizationId = @OrganizationId)
AND (@RepositoryId IS NULL OR RepositoryId = @RepositoryId)
AND
(
    @AfterObservedAtUtc IS NULL
    OR ObservedAtUtc > @AfterObservedAtUtc
    OR (ObservedAtUtc = @AfterObservedAtUtc AND UsageFactId > @AfterUsageFactId)
)
ORDER BY ObservedAtUtc ASC, UsageFactId ASC;
"""

    /// Inserts an archived replay row without repopulating the hot SQL payload, preserving replay idempotency by UsageFactId.
    [<Literal>]
    let TryInsertReplayedArchivedRawUsageFact =
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
    ObservedAtUtc,
    ArchiveState,
    ArchiveBlobName,
    ArchiveChecksumSha256Hex,
    ArchiveByteLength,
    ArchiveVerifiedAtUtc,
    ArchivedAtUtc
)
SELECT
    @UsageFactId,
    NULL,
    @CorrelationId,
    @FactKind,
    @OwnerId,
    @OrganizationId,
    @RepositoryId,
    @StoragePoolId,
    @Quantity,
    @ObservedAtUtc,
    @ArchiveStateArchived,
    @ArchiveBlobName,
    @ArchiveChecksumSha256Hex,
    @ArchiveByteLength,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
WHERE NOT EXISTS
(
    SELECT 1
    FROM ops.RawUsageFact WITH (UPDLOCK, HOLDLOCK)
    WHERE UsageFactId = @UsageFactId
);
"""

    /// Declares the temporary table variable used for batched temporary-hot payload restore.
    [<Literal>]
    let DeclareRehydratedRawUsageFactBatch =
        """
DECLARE @RehydrationRows table
(
    UsageFactId uniqueidentifier NOT NULL PRIMARY KEY,
    RawPayload varbinary(max) NOT NULL,
    ArchiveBlobName nvarchar(512) NOT NULL,
    ArchiveChecksumSha256Hex char(64) NOT NULL,
    ArchiveByteLength bigint NOT NULL
);
"""

    /// Restores or refreshes archived raw payload bytes only for exact SQL Blob pointer matches.
    [<Literal>]
    let RehydrateArchivedRawUsageFactPayloadBatch =
        """
UPDATE target
SET
    RawPayload = source.RawPayload,
    RehydrationExpiresAtUtc =
        CASE
            WHEN target.RehydrationExpiresAtUtc IS NOT NULL
                 AND target.RehydrationExpiresAtUtc > @RehydrationExpiresAtUtc THEN target.RehydrationExpiresAtUtc
            ELSE @RehydrationExpiresAtUtc
        END
OUTPUT inserted.UsageFactId
FROM ops.RawUsageFact AS target
INNER JOIN @RehydrationRows AS source
    ON source.UsageFactId = target.UsageFactId
WHERE target.ArchiveState = @ArchiveStateArchived
AND target.ArchiveBlobName = source.ArchiveBlobName
AND target.ArchiveChecksumSha256Hex = source.ArchiveChecksumSha256Hex
AND target.ArchiveByteLength = source.ArchiveByteLength
AND target.ArchiveBlobName IS NOT NULL
AND target.ArchiveChecksumSha256Hex IS NOT NULL
AND target.ArchiveByteLength IS NOT NULL;
"""

    /// Clears one batch of expired temporary-hot raw payload bytes while retaining archived SQL pointer authority.
    [<Literal>]
    let CleanupExpiredRehydratedRawUsageFactPayloads =
        """
UPDATE TOP (@BatchSize) ops.RawUsageFact
SET
    RawPayload = NULL,
    RehydrationExpiresAtUtc = NULL
WHERE ArchiveState = @ArchiveStateArchived
AND RawPayload IS NOT NULL
AND RehydrationExpiresAtUtc IS NOT NULL
AND RehydrationExpiresAtUtc <= @ExpiresBeforeUtc
AND ArchiveBlobName IS NOT NULL
AND ArchiveChecksumSha256Hex IS NOT NULL
AND ArchiveByteLength IS NOT NULL;

SELECT @@ROWCOUNT;
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
    ArchiveVerifiedAtUtc = SYSUTCDATETIME(),
    LastArchiveFailureReason = NULL,
    LastArchiveFailureAtUtc = NULL,
    ArchiveFailureCount = 0,
    ArchiveRetiredAtUtc = NULL
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
    ArchivedAtUtc = SYSUTCDATETIME(),
    LastArchiveFailureReason = NULL,
    LastArchiveFailureAtUtc = NULL,
    ArchiveFailureCount = 0,
    ArchiveRetiredAtUtc = NULL
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
    AND (RawPayload IS NULL OR RehydrationExpiresAtUtc IS NOT NULL)
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

    /// Records a row-scoped archive failure and retires repeatedly failing rows until operator repair clears the evidence.
    [<Literal>]
    let RecordRawUsageFactArchiveFailure =
        """
UPDATE ops.RawUsageFact
SET
    LastArchiveFailureReason = @LastArchiveFailureReason,
    LastArchiveFailureAtUtc = SYSUTCDATETIME(),
    ArchiveFailureCount = ArchiveFailureCount + 1,
    ArchiveRetiredAtUtc =
        CASE
            WHEN ArchiveFailureCount + 1 >= @ArchiveFailureRetirementThreshold THEN SYSUTCDATETIME()
            ELSE ArchiveRetiredAtUtc
        END
WHERE UsageFactId = @UsageFactId
AND ArchiveState IN (@ArchiveStateHot, @ArchiveStateVerified);
"""

    /// Lists row-scoped archive failures with enough identifiers for operator repair or disposal.
    [<Literal>]
    let SelectRawUsageFactArchiveFailuresForOperatorRepair =
        """
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
