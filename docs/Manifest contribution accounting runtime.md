# Manifest contribution accounting runtime

Manifest contribution accounting turns every completed Reference into exact relationship and counter updates. The
foreground contract is identical for `Promotion`, `Commit`, `Checkpoint`, `Save`, `Tag`, `External`, and `Rebase`:
Grace persists the Reference, waits for deterministic Service Bus acceptance, and returns while accounting continues
asynchronously. This runbook describes the current local runtime and the one supported command for publishing its
canonical evidence. The command measures a fixed fixture; it is not a production probe or a deployment benchmark.

## Runtime topology and state ownership

The grouped measurement starts one Aspire host inside one selected `dotnet test` process. That host provides Grace
Server, Orleans, the Cosmos DB emulator, Azurite, the Service Bus emulator and its SQL dependency, and Redis. The eight
scenarios run serially in that one host. The publication script does not start an AppHost process of its own.

State has deliberately separate roles:

- Reference events are the persisted trigger for manifest contribution work.
- `ManifestContributionWorkflowActor` owns durable per-target workflow progress and replay decisions.
- `DirectoryVersionActor` owns the retained manifest and therefore determines contribution retention.
- Exact relationships and repository counters are durable Cosmos-backed accounting evidence.
- ContentBlock physical ranges own active-manifest counts; shared ContentBlocks are updated once per physical range.
- Service Bus owns delivery, retry, settlement, and dead-letter state for the event envelope.
- Redis `8.6.3` caches recent repository-counter results. It is an accelerator only and is never authoritative.

## Telemetry and structured logs

The meter and activity source both use `Grace.ManifestContributionAccounting`. The activity name is
`manifest-contribution.process-message`. Message, correlation, delivery, Reference, repository, and DirectoryVersion
identifiers are activity tags, not metric dimensions.

The instruments are:

- `grace.manifest_contribution.messages`;
- `grace.manifest_contribution.processing.duration`, in milliseconds;
- `grace.manifest_contribution.relationship.writes`;
- `grace.manifest_contribution.redis.operations`;
- `grace.manifest_contribution.repair.actions`.

Metric tags are bounded to `stage`, `outcome`, `reference_type`, `relationship_kind`, `operation`, `direction`, and
`is_replay`. The processor's relevant structured log templates are:

- `GraceEvent handler failed for message {MessageId} (CorrelationId: {CorrelationId}); abandoning for broker retry.`
- `Started Grace pub-sub listener for topic {TopicName} / subscription {SubscriptionName}.`
- `Redis repository counter recent-result {Operation} failed; using nonauthoritative fallback {Fallback}.`

## Service Bus behavior and settings

Grace disables automatic completion. A parsed, handled delivery is completed explicitly. A handler failure is
abandoned for broker retry. Malformed GraceEvent JSON is dead-lettered with reason `MalformedGraceEvent`. If complete,
abandon, or dead-letter settlement itself fails, processing reports a settlement failure instead of claiming success.

The Grace event processor uses four concurrent calls and a prefetch count of 16. The local emulator configuration uses
a one-hour topic and subscription TTL, a one-minute subscription lock, a maximum delivery count of 10, no sessions,
and no forwarding. The Grace event topic does not require duplicate detection. The separate operational-facts topic
uses a five-minute duplicate-detection window. The dead-letter fixture proves the selected test subscription reaches
delivery count 11 and keeps production telemetry isolated.

These are current local and configured values, not a throughput target. The fixture does not establish long-handler
lock-renewal behavior.

## Supported evidence command

From the repository root, with Git, .NET, Docker, and Docker Desktop available, run:

```powershell
pwsh ./scripts/measure-manifest-contribution-accounting.ps1 `
  -OutputDirectory ./artifacts/manifest-accounting-measurements
```

`OutputDirectory` is required and may be relative or absolute. It must not exist. The command never overwrites or
merges an existing directory. `Scenario`, `ReferenceCount`, `Concurrency`, and other selection or load inputs are not
supported and fail during PowerShell parameter binding.

The script requires a clean worktree, builds `Grace.Server.Tests` once in Release, and runs exactly the explicit
`ManifestContributionGroupedMeasurementTests` selection with `--no-build`. The selected process owns the only Aspire
host. A successful command validates, stages, and atomically publishes the packet. A failure removes only the
script-owned sibling staging directory and leaves the requested destination absent.

## Fixed scenario plan

The scenario order and cardinalities are fixture-owned and cannot be tuned:

1. `baseline` uses three selected topology assets and proves the normal exact relationship and count flow.
2. `hot-manifest` uses three References contributing the same hot manifest and expects three Reference roots and three
   manifest relationships.
3. `highly-shared` uses three References sharing one manifest relationship and one active physical contribution.
4. `duplicate-backlog` replays a fixed duplicate Save backlog while Grace Server is stopped and proves durable state
   remains unchanged. Save is fixture data, not a specialized accounting path.
5. `redis-restart` restarts the nonauthoritative cache and proves the next fixture Reference adds exactly one logical
   contribution through the Reference-wide path.
6. `server-restart` restarts Grace Server and proves a persisted envelope completes without duplicating state.
7. `repair` removes one Reference root, dry-runs diagnosis, and executes exactly one republication action.
8. `dead-letter` drives one isolated malformed delivery through the broker's fixed retry limit to delivery count 11.

The accepted fixture emits nine passed summaries, 104 unique passed assertions, 52 samples, and 32 raw stimulus phase
snapshots. Publication rejects any different closure rather than treating it as another supported workload.

## Evidence packet

The published tree is:

```text
run.json
assertions.json
samples.ndjson
summary.json
artifact-hashes.json
raw/
  run.ndjson
  samples.ndjson
  assertions.ndjson
  summaries.ndjson
  artifact-hashes.json
logs/
  baseline.jsonl
  hot-manifest.jsonl
  highly-shared.jsonl
  duplicate-backlog.jsonl
  redis-restart.jsonl
  server-restart.jsonl
  repair.jsonl
  dead-letter.jsonl
```

The five `raw/` files are byte-for-byte copies of the grouped fixture output. `samples.ndjson` is also copied
byte-for-byte at the packet root. `assertions.json` is the stable raw assertion order. `summary.json` recomputes
scenario outcomes, assertion and sample totals, phase-snapshot count, and numeric sample aggregates. Each scenario log
is a deterministic navigation projection that names its raw source file and line.

The root `artifact-hashes.json` contains a normalized relative path and lowercase SHA-256 for every other file, ordered
deterministically; it does not hash itself. Records are limited to 65,536 UTF-8 bytes, files to 2 MiB, and the packet to
16 MiB. Raw hashes are verified before normalization. Malformed, unsupported, failed, skipped, duplicated, unknown,
oversized, secret-bearing, stale-SHA, or non-recomputable evidence fails publication.

## Exact-SHA provenance

Evidence uses two truthful commits:

1. Commit and push the script, tests, and this runbook.
2. Run the supported command from that exact clean source commit.
3. Confirm `run.json.sourceGitSha` and `raw/run.ndjson` `CommitSha` name that source commit.
4. Commit only the generated packet as the immediately following artifact commit.

The accepted Product V1 packet uses these distinct identities:

- clean measured source `60da9740236e36bfab770a1f246f0d993740b7fc`;
- immediate artifact-only child `dce9fe0a2677459e521b30737c296f917f7eb701`; and
- Epic merge `96c3c09300d3fbe0d65fad309afe2bc3983dd280`.

The final audit commit is a fourth identity. Audit-only documentation and acceptance-proof changes do not make the
packet stale because the audit proves that the measured publisher, runtime, fixture, production, public, durable, and
generated surfaces are unchanged. Any change to one of those measured surfaces does make the packet stale and requires
a new clean run. The artifact child, Epic merge, and final audit commit are never presented as the source exercised by
the fixture.

`run.json` records the ten-entry `localClaims` set and the six-entry `azureOnlyUnknowns` set. The independent final
audit recomputes their exact separation rather than inferring Azure behavior from local evidence.

## Interpreting the evidence

The local packet supports only claims directly represented by its raw samples and assertions: foreground call count
and local latency, relationship writes and convergence, local worker throughput and backlog drain, serialized actor
document sizes exposed by the fixture, directly exposed exact-relationship or query request charge, local operation
concentration, Redis reconnect observations, ContentBlock sharing, emulator delivery and dead-letter behavior, and
deterministic correctness results.

It does not establish complete Orleans persistence request charge, Azure partition heat or throttling, failover,
availability, cross-region behavior, production SLO thresholds, HA/DR guarantees, or long-handler lock renewal outside
the fixture. Local emulator latency and request charge are not Azure evidence.

There is no production SLO, mirrored queue, automatic repair, production probe, HA/DR promise, or Azure extrapolation
in this command or packet.

## Diagnosis and repair

Use [Manifest contribution diagnosis](Manifest%20contribution%20diagnosis.md) for bounded read-only investigation.
Use [Manifest contribution repair](Manifest%20contribution%20repair.md) only after diagnosis identifies a supported,
signed repair plan; repair remains dry-run-first and operator-directed.

For the broader local Aspire resource map and prerequisites, see
[Grace Aspire setup](../src/docs/ASPIRE_SETUP.md).
