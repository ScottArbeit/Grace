# Operations Data Migrations

`Grace.Operations.Data` owns the EF Core model and migrations for the Azure SQL operations store. The current baseline
schema is intentionally narrow:

- `ops.RawUsageFact` stores immutable `UsageFact` rows with `UsageFactId` as the duplicate-delivery boundary and keeps
  the exact accepted broker payload in `RawPayload` for replay and audit.
- `ops.UsageAggregateMinute` stores one aggregate row per fact kind, Grace scope, storage pool, and UTC minute.
- `ops.ChargePreviewLine` stores deterministic provisional customer charge lines rebuilt from compact immutable usage
  facts and complete effective pricing. A rebuild atomically replaces one customer/repository/half-open-period scope;
  these rows are neither invoices nor immutable ledger entries.
- The EF migrations history table lives in `ops.__EFMigrationsHistory` so operations schema state stays with the
  operations schema.

The ingestion hot path still uses reviewed raw SQL for the durable insert and aggregate update. That path preserves the
`UsageFactId` idempotency lock and the aggregate `MERGE ... WITH (HOLDLOCK)` behavior that the worker depends on.

## Hot/Cold Raw Payload Archive

Raw payloads stay hot in SQL only for the configured Operations archive retention window. When archive Blob settings
are supplied, the Operations archive worker writes one deterministic compressed JSONL Blob per `UsageFactId` after that
window and records Blob authority before it clears `ops.RawUsageFact.RawPayload`. Existing ingestion-only local hosts
that do not provide archive settings continue to start without registering the archive hosted service; partial archive
settings still fail startup instead of silently running with missing Blob authority.

Archive Blob names are deterministic and scope-qualified:

```text
usage-facts/v1/observedYear=<yyyy>/observedMonth=<MM>/ownerId=<owner-guid>/organizationId=<organization-guid>/repositoryId=<repository-guid>/usageFactId=<usage-fact-guid>.jsonl.gz
```

Each Blob contains one gzip-compressed JSONL record with archive schema version, usage fact identity, Grace scope,
storage pool, quantity, observed UTC timestamp, and the exact accepted broker payload as base64. SQL stores:

- `ArchiveState`, where `0` is hot, `1` is Blob-verified with hot payload retained, and `2` is archived with hot
  payload cleared.
- `ArchiveBlobName`, the deterministic Blob pointer.
- `ArchiveChecksumSha256Hex`, the SHA-256 checksum of the compressed Blob bytes.
- `ArchiveByteLength`, the exact compressed Blob byte length.
- `ArchiveVerifiedAtUtc` and `ArchivedAtUtc`, the two ordering points for partial-success resume.

The worker verifies Blob checksum and byte length from Blob storage before recording `ArchiveState = 1`, then verifies
the same Blob pointer again before clearing `RawPayload` and setting `ArchiveState = 2`. If the Blob is missing,
corrupt, or different from the SQL pointer, cleanup fails loudly and the hot payload is not cleared. A retry can resume
from `ArchiveState = 1` without rewriting the Blob.

## Monthly Partitioning

The current raw fact and minute aggregate tables are monthly partitioned by the UTC time columns used by range queries
and future retention windows:

- `ops.RawUsageFact` uses the shared `PF_ops_OperationsUsageMonthUtc` partition function through
  `PS_ops_OperationsUsageMonthUtc`, with a clustered index on `(ObservedAtUtc, UsageFactId)`.
- `ops.RawUsageFact` keeps `PK_ops_RawUsageFact` as a nonclustered primary key on `UsageFactId` so duplicate delivery
  is still rejected by the durable usage-fact identity instead of by the partition month.
- `ops.UsageAggregateMinute` keeps its logical primary key and places the clustered key on the partition scheme by
  `BucketStartUtc`, which is already part of the aggregate key.
- Scope-and-kind range indexes are rebuilt on the same partition scheme using `ObservedAtUtc` or `BucketStartUtc`.

The initial partition function uses reviewed month-start UTC boundaries from January 2026 through January 2029 with
`RANGE RIGHT`. The scheme maps all partitions to `[PRIMARY]`. That is the Azure SQL-compatible and local SQL Server
test equivalent for this slice: it proves partition functions, partition schemes, and aligned index placement without
requiring Azure-only filegroup behavior.

Future retention or archive migrations must split the partition function before a new retention window needs an
additional month. Do not add partition maintenance to worker startup or request-time bootstrap code.

Rollback drops the partition-aligned indexes, restores the baseline unpartitioned keys and range indexes on `[PRIMARY]`,
then removes the partition scheme and function after no indexes reference them.

## Raw SQL Escape Hatch

Use EF migration builder APIs for ordinary tables, columns, keys, and indexes. Raw SQL is allowed only inside migration
classes or reviewed helper modules used directly by migrations when SQL Server or Azure SQL physical design needs a
shape EF cannot express clearly. Valid examples include provider-specific index options, partitioning, compression,
online rebuild options, lock hints for data backfills, and guarded data movement for a future reviewed migration.

Do not put SQL Server physical-design choices in worker startup, request handlers, or ad hoc runtime bootstrap code.
When raw SQL is used in a migration, keep it deterministic, name the Grace invariant it protects, and cover the emitted
schema or behavior with Operations migration tests.

## Callable Charge Previews

The worker registers an internal `IChargePreviewRebuilder` that callers can invoke with an explicit UTC half-open
period. The rebuild reads compact `RawUsageFact` index columns, so archived facts remain eligible when `RawPayload` is
empty and Blob rehydration is neither performed nor required. Complete pricing is selected at each fact's observed
timestamp, quantities are summed per applicability segment, and each line is rounded once to whole currency micros.

This leaf intentionally adds no timer, schedule, billing-period state, close operation, HTTP route, or ledger posting.
Missing pricing fails the rebuild and leaves the previously committed complete preview unchanged.
