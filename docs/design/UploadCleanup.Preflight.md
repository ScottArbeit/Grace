# Unfinished upload cleanup preflight

[Issue #1083](https://github.com/ScottArbeit/Grace/issues/1083) extends the existing UploadSession and
ContentBlockMetadata owners so unfinished payload can be reclaimed without deleting another upload's or completed
manifest's content. The [portable capture](UploadCleanup.Preflight.json) contains exact source bytes, per-file SHA-256
hashes, results and original execution receipts. No production credentials are included; the emulator key is public.

## Evidence and limits

| Experiment | Result | What ran |
| --- | --- | --- |
| Block publication and cleanup | 26 checks passed | Real Azurite effects across eight executable processes; local JSON models for holder and actor persistence decisions. |
| Provider boundaries and signed staging grants | 15 checks passed | Real Azurite conditional PUT/delete and signed SAS requests, including four staging controls. |
| Retry clock and immutable reminders | 19 checks passed | Pure PowerShell state model with persisted JSON recovery inputs; no hosted reminder service. |

Both request arrival orderings ran against storage. A payload PUT using a deleted or replaced placeholder's exact ETag
returns 412 and cannot recreate content. If payload wins first, its ETag changes and cleanup must observe and persist
the new condition before deleting. Repeated deletion can return 412 after a successful delete; HEAD establishes absence.
An unconditional or `IfNoneMatch` payload request demonstrably recreates deleted bytes and is excluded from the design.
Signed SAS controls establish the same exact-ETag behavior for staging. Request delivery was ordered by the harness;
these were not suspended sockets or SDK timeout experiments.

The [Library storage experiment](Libraries.Storage-Experiment.md) covers unchanged Cosmos provider behavior:
stale ETags, changed failed buffers, duplicate creation and completed writes whose responses were lost. The current
`Microsoft.Orleans.Persistence.Cosmos 10.2.2-hpk.1` package SHA-256 is
`E930D0461935120313EEF0D41CD38AE808993A2DF3CBF10EFFDD64571D384575`; its captured source archive is
`2F931BA4C6BBEBCC38B338C7DE0AE2F1907595FFAAC6621BF9683EA43768450C`. These matches support reuse of provider
semantics only. They do not establish serialization or integration of this change's new event fields.

## Selected effect order

1. Persist the session's existing block intent before any staging or block publication work.
2. Create only an empty staging placeholder and persist its exact ETag before grant delivery. Cap SAS validity at
   the session retry deadline. The SDK decodes `graceContentBlockETag` from the existing URI fragment and requires
   `IfMatch` for its single PUT. Missing ETag requires a fresh grant; it never enables an unconditional fallback.
3. Before permanent publication, the block actor persists this session's hold and placement, creates or observes an
   empty placeholder, and persists its exact ETag. Only then may it send a single payload PUT with `IfMatch`.
4. Recover a lost response by validating complete stored content while retaining the hold. Empty or corrupt payload
   never counts as a confirmed block. State write failure stops the activation before any follow-on storage effect.
5. Close the session retry window before releasing its intent inventory. Keep holds for selected manifest blocks
   until their metadata merges are durable. Omitted extra uploads can release normally.
6. Release records the session identity even when its acquire request has not arrived. The retained event history
   rejects a delayed acquire after cleanup. Blocks with any completed metadata remain outside this deletion path.
7. With no metadata or holders, persist a deletion reservation, observe and persist the exact blob ETag, then delete
   conditionally. Reconcile changed conditions and lost responses; retain failures for retry before compacting session state.

The block actor keeps exactly one latest upload-state snapshot alongside all non-upload metadata events. Replacing that
snapshot occurs in the same conditional state write as the transition; its complete retirement set remains indefinite.
This keeps persisted retirement history linear while preserving hold, placement, ETag and deletion recovery evidence.

Delayed empty-placeholder creation can leave zero-byte overhead. The design promises payload cleanup, not permanent
404 responses. It introduces no reference count, catalog, SQL table, background service, usage fact or billing producer.

## Retry deadline

Start, a genuinely new confirmed address, a new valid reuse range and first completion set one hour of retry time.
Replay, polling, discovery, intents and grant requests do not count as progress. Library preparations share this clock
and retain a full hour after completion. Actor activation does not renew it.

Each deadline has a deterministic immutable reminder ID. The first reminder is durable before Started is accepted.
A callback ensures the current deadline's distinct reminder before acknowledging its obsolete delivery. A process
startup later than the last clock advance, or processing more than 120 seconds overdue, grants one hour from the first
observed recovery and persists that grant. A closed window cannot reopen. This conservatively detects recovery;
it does not measure outages entirely between observations and may retain data longer under overload.

## Extract and inspect

The extractor verifies every hash and refuses an existing destination. The captured scripts use fixed loopback ports,
unique emulator names and retained stopped containers. Inspect those scripts before rerunning them.

PowerShell:

```powershell
pwsh ./docs/design/UploadCleanup.Preflight.ps1 -OutputDirectory ./artifacts/upload-cleanup-capture
pwsh ./artifacts/upload-cleanup-capture/block/Run-Experiment.ps1
pwsh ./artifacts/upload-cleanup-capture/provider/Run-Experiment.ps1
pwsh ./artifacts/upload-cleanup-capture/provider/Run-ClockModel.ps1
```

bash / zsh:

```bash
pwsh ./docs/design/UploadCleanup.Preflight.ps1 -OutputDirectory ./artifacts/upload-cleanup-capture
```

The provider runners use Windows PowerShell networking commands. Extraction itself is portable; running those captured
provider scripts requires the original Windows/Docker environment. Their results remain historical evidence with the
exact hashes in the capture. Current source builds, codec tests and hosted actor tests are reported separately in the PR.

## Hosted validation and Local debug connections

The hosted Linux tests exposed Cosmos state writes waiting inside HTTP send before response headers. JSON serialization
had completed; the captures did not establish a size threshold or a 429 retry. The stopped-server Library fixture also
needed to restart its shared server when an offline assertion failed, so one failure would not prevent later tests from running.

A controlled CI trial changed only the existing Local debug actor-storage client's pooled connection lifetime to one
millisecond. [Validate run 34474510757](https://github.com/ScottArbeit/Grace/actions/runs/34474510757) passed, including
322 hosted tests with 36 skipped, after the pooled configuration repeatedly failed the same upload/Library paths.
This matches a [reported vNext emulator connection issue](https://github.com/Azure/azure-cosmos-dotnet-v3/issues/6000);
the precise internal race is not established by Grace's captures. A separate synthetic Linux SDK 3.62.1 size comparison
passed all 16 conditional writes in both pooled and connection-retirement modes, so that smaller comparison did not reproduce the fault.

The setting applies when `DebugEnvironment` is `Local` and Cosmos managed identity is disabled, matching the existing
Gateway/certificate-bypass branch. It is selected by mode, not by inspecting the endpoint hostname. The short lifetime
retires aged connections between requests; it does not cancel an in-flight request or guarantee a fresh connection for
every request. Other modes and managed-identity client paths retain their existing settings. Temporary diagnostics are
removed from the final candidate; its required CI result is recorded separately in PR #1086.
