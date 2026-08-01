# Manifest contribution accounting Product V1 audit

**Status:** Plan-ready contract implemented; final audit evidence is maintained by
`scripts/tests/audit-manifest-contribution-accounting.Tests.ps1`.

**Quality contract:** Product V1 final integration and contract audit.

**Evidence baseline:** measured source `60da9740236e36bfab770a1f246f0d993740b7fc`, artifact-only child
`dce9fe0a2677459e521b30737c296f917f7eb701`, and Epic merge
`96c3c09300d3fbe0d65fad309afe2bc3983dd280`.

## Outcome and invariant

Grace persists a Reference, waits for deterministic Service Bus acceptance of
`Reference/{ReferenceId}/Created`, and returns without repository-size traversal. Background processing rereads the
current Reference and DirectoryVersion actors, converges exact relationships, applies bounded repository counts, and
updates physical ContentBlock contribution only on repository zero transitions.

This contract is identical for `Promotion`, `Commit`, `Checkpoint`, `Save`, `Tag`, `External`, and `Rebase`. Commit and
Save are fixture choices in some focused proofs; they are not accounting categories. `DirectoryVersionActor`, rather
than `ReferenceType`, owns manifest retention.

## Requirement coverage

| ID | Disposition | Current implementation and contract surface | Current proof and accepted evidence |
| -- | ----------- | ------------------------------------------- | ----------------------------------- |
| MCA-00 | Implemented and proven | Exact identities, bounded reads, and store interfaces in `src/Grace.Types/ManifestContributionAccounting.Types.fs` and `src/Grace.Actors/ManifestContributionAccounting.Actor.fs`. Public/runtime behavior was not activated by this scaffold. | `src/Grace.Types.Tests/ManifestContributionAccounting.Types.Tests.fs`; merge `ea1828b6`. |
| MCA-01 | Implemented and proven | Reference persistence precedes deterministic broker acceptance; replay reconstructs the persisted Created event; accounting is background-only in `src/Grace.Actors/Reference.Actor.fs` and `src/Grace.Server/ManifestContributionAccounting.Server.fs`. | `src/Grace.Server.Unit.Tests/Reference.Actor.Tests.fs`, `src/Grace.Server.Unit.Tests/ManifestContributionAccounting.Server.Tests.fs`, and public Commit proof in `src/Grace.Server.Tests/ManifestContributionAccounting.Server.Tests.fs`; merge `17c9f3e7`. |
| MCA-02 | Implemented and proven | Caller-owned stable Reference identity reaches Branch/Reference producers, HTTP, CLI, SDK, OpenAPI, and generated clients through `src/Grace.Shared/Parameters/Branch.Parameters.fs`, `src/Grace.SDK/Branch.SDK.fs`, `src/Grace.CLI/Command/Branch.CLI.fs`, and `src/OpenAPI/Branch.Components.OpenAPI.yaml`. | Branch, CLI, OpenAPI, SDK-generator, and retry tests; generated freshness manifests; merge `59ef42c9`. |
| MCA-03 | Implemented and proven | Reference-root and parent-child relationships converge from actor rereads; shared traversal continues only on incoming 0-to-1 in `src/Grace.Server/ManifestContributionAccounting.Server.fs`. | Diamond, nested-manifest, stale-source, and shared-child proofs in `src/Grace.Server.Unit.Tests/ManifestContributionAccounting.Server.Tests.fs`; merge `6e4b3d4d`. |
| MCA-04 | Implemented and proven | Bounded counter/workflow snapshots and ten-minute nonauthoritative Redis replay aid live in `src/Grace.Actors/RepositoryContentCounter.Actor.fs`, `src/Grace.Actors/ManifestContributionWorkflow.Actor.fs`, and `src/Grace.Server/RepositoryCounterRecentResult.Server.fs`. | Counter, workflow, Redis, StoragePool, and integration tests in `src/Grace.Server.Unit.Tests` and `src/Grace.Server.Tests`; merge `d2eba214`. |
| MCA-05 | Implemented and proven | Logical deletion preserves retention. Physical deletion checks incoming evidence, converges outgoing relationships retain-first, verifies absence, and only then clears DirectoryVersion state in `src/Grace.Actors/DirectoryVersion.Actor.fs`. Reference deletion removes only its exact root. | Deletion, restart, unknown-removal, and all-ReferenceType proofs in `src/Grace.Server.Unit.Tests/ManifestContributionAccounting.Server.Tests.fs` and `src/Grace.Server.Unit.Tests/Reference.Actor.Tests.fs`; merge `9d8f5933`. |
| MCA-06 | Implemented and proven | SystemAdmin-only bounded diagnosis route and script in `src/Grace.Server/ManifestContributionDiagnosis.Server.fs` and `scripts/diagnose-manifest-contribution.ps1`; intentionally excluded from public OpenAPI and generated clients. | `scripts/tests/diagnose-manifest-contribution.Tests.ps1`, `src/Grace.Server.Unit.Tests/ManifestContributionDiagnosis.Server.Tests.fs`, and `docs/Manifest contribution diagnosis.md`; merge `ef5d19f8`. |
| MCA-07 | Implemented and proven | Dry-run-first SystemAdmin repair with signed-report rereads, finite actions, retain-safe outcomes, and final diagnosis in `src/Grace.Server/ManifestContributionRepair.Server.fs` and `scripts/repair-manifest-contribution.ps1`; intentionally excluded from public OpenAPI and generated clients. | `scripts/tests/repair-manifest-contribution.Tests.ps1`, `src/Grace.Server.Unit.Tests/ManifestContributionRepair.Server.Tests.fs`, and `docs/Manifest contribution repair.md`; merge `2e990aee`. |
| MCA-08 | Implemented and proven | Issue #736 is the completed operational-proof parent. Its accepted topology, state ownership, scenario boundaries, and local-versus-Azure claim boundary are implemented by MCA-08A and MCA-08B-R1 through R8. | Child merge sequence `3074a723` through `eda17e2c`, followed by the MCA-08C packet. |
| MCA-08A | Implemented and proven | Typed complete/abandon/dead-letter settlement and bounded telemetry in `src/Grace.Server/Notification.Server.fs` and `src/Grace.Server/ManifestContributionTelemetry.Server.fs`. | `src/Grace.Server.Unit.Tests/ManifestContributionTelemetry.Server.Tests.fs`; merge `3074a723`. |
| MCA-08B | Superseded and rejected as implementation | Issue #751 and PR #754 combined several invariant families and were not merged. No current product or proof surface depends on their branch. | Issues #755 through #763 are the accepted replacement sequence; the historical branch is research evidence only. |
| MCA-08B-R0 | Implemented as readiness evidence | Isolated-host feasibility and exact assertion contract were closed in Issue #755. Not applicable to maintained product code because this leaf intentionally made no repository change. | Issue #755 decision packet; replacement leaves R1 through R8 own maintained proof. |
| MCA-08B-R1 | Implemented and proven | Baseline selected-process measurement support in `src/Grace.Server.Tests/ManifestContributionBaseline.Measurement.Tests.fs`. | Baseline and pure measurement tests; merge `1b4cd8bf`. |
| MCA-08B-R2 | Implemented and proven | Hot-manifest and highly-shared topology/cardinality fixtures in `src/Grace.Server.Tests/ManifestContributionTopologyCardinality.Measurement.Tests.fs`. | Raw and published `hot-manifest` and `highly-shared` assertions; merge `53f7166a`. |
| MCA-08B-R3 | Implemented and proven | Duplicate-backlog stop/start and idempotent replay fixture in `src/Grace.Server.Tests/ManifestContributionDuplicateBacklog.Measurement.Tests.fs`. | Raw and published `duplicate-backlog` assertions; merge `a865e29e`. |
| MCA-08B-R4 | Implemented and proven | Redis restart fixture in `src/Grace.Server.Tests/ManifestContributionRedisRestart.Measurement.Tests.fs`. | Raw and published `redis-restart` assertions; merge `7e97cded`. |
| MCA-08B-R5 | Implemented and proven | Grace Server restart/replay fixture in `src/Grace.Server.Tests/ManifestContributionServerRestart.Measurement.Tests.fs`. | Raw and published `server-restart` assertions; merge `733d8a98`. |
| MCA-08B-R6 | Implemented and proven | Production diagnosis/repair republication attribution fixture in `src/Grace.Server.Tests/ManifestContributionRepair.Measurement.Tests.fs`. | Raw and published `repair` assertions; merge `76e14d5a`. |
| MCA-08B-R7 | Implemented and proven | Isolated broker dead-letter fixture plus pure malformed/settlement proof in `src/Grace.Server.Tests/ManifestContributionDeadLetter.Measurement.Tests.fs` and `src/Grace.Server.Unit.Tests/ManifestContributionDeadLetterMeasurement.Tests.fs`. | Raw and published `dead-letter` assertions; merge `ef0deffe`. |
| MCA-08B-R8 | Implemented and proven | One-host grouped composition in `src/Grace.Server.Tests/ManifestContributionGroupedRuntime.fs` and `src/Grace.Server.Tests/ManifestContributionGrouped.Measurement.Tests.fs`. | Nine summaries, 104 unique assertions, 52 samples, and 32 stimulus snapshots in the accepted packet; merge `eda17e2c`. |
| MCA-08B-R8A | Closed without a product change | The corrected one-host real-Cosmos witness did not reproduce the earlier local DedupeIndex/Cosmos transport incident. Not applicable to current code because Issue #774 established no deterministic RED and authorized no speculative fix. | Issue #774 closure evidence; recurrence of the same signature remains an owner stop. |
| MCA-08C | Implemented and proven | Sole full-run publisher and validation contract in `scripts/measure-manifest-contribution-accounting.ps1`; maintained packet begins at `artifacts/manifest-accounting-measurements/run.json`. | Publisher tests in `scripts/tests/measure-manifest-contribution-accounting.Tests.ps1`; measured source `60da9740`, artifact child `dce9fe0a`, and Epic merge `96c3c093`. |
| MCA-09 | Implemented by this audit | This coverage ledger, corrected context/runbooks, and `scripts/tests/audit-manifest-contribution-accounting.Tests.ps1` verify current-head contract and evidence freshness without changing product behavior. | The audit command, PowerShell parsing, focused tests, generated freshness, MarkdownLint, and `git diff --check`. |

Issue #751 and PR #754 are superseded research evidence. They are rejected as an implementation source and were not
merged. Issues #755 through #763 are the accepted MCA-08B replacement sequence.

## Epic requirement coverage

| Epic #727 requirement | Primary implementation | Proof and disposition |
| ---------------------- | ---------------------- | --------------------- |
| Exact Reference-to-root and first manifest relationship | MCA-01 in `src/Grace.Server/ManifestContributionAccounting.Server.fs` | Public Commit tracer and type-neutral duplicate convergence. Implemented and proven. |
| Stable identity across producers and clients | MCA-02 across Branch parameters, actors, CLI, SDK, OpenAPI, and generated clients | Focused public/generated proof and freshness checks. Implemented and proven with the named response-loss residual risks. |
| Exact parent-child relationships and shared-DAG stop | MCA-03 in the accounting handler and exact store | Nested diamond, stale-source, shared-child, and incoming 0-to-1 proofs. Implemented and proven. |
| Bounded counter, Redis recent result, and StoragePool transitions | MCA-04 in counter, workflow, Redis, and ContentBlock actors/services | Counter/workflow/Redis tests and grouped scenarios. Implemented and proven with the accepted Redis compound-interruption risk. |
| Logical retention and restart-safe physical deletion | MCA-05 in `src/Grace.Actors/DirectoryVersion.Actor.fs` | Logical delete/undelete, unknown removal, restart, and outgoing-absence proof. Implemented and proven with DEC-MCA-013 accepted. |
| Read-only diagnosis | MCA-06 internal route and operator script | Selector, bound, SHA, authorization, zero-write, and retain-safe outcome proof. Implemented and proven. |
| Dry-run-first bounded repair | MCA-07 internal route and operator script | Signed reread, finite action, partial failure, post-diagnosis, and all-ReferenceType republication proof. Implemented and proven. |
| Failure, telemetry, DLQ, RU, throughput, and latency proof | MCA-08A plus MCA-08B-R1 through R8 | Accepted local packet; Azure-only unknowns remain explicit. Implemented and proven within the local Product V1 boundary. |
| All-ReferenceType and contract-propagation audit | MCA-09 ledger and executable audit | Seven exact enum members, type-neutral implementation/proof, generated freshness, runbook contracts, and unchanged measured surfaces. Implemented and proven on the final audit head. |

## Contract propagation disposition

| Surface | Disposition | Evidence |
| ------- | ----------- | -------- |
| Shared DTOs, events, and persisted shapes | Implemented and unchanged by MCA-09 | `src/Grace.Types/ManifestContributionAccounting.Types.fs`, `src/Grace.Types/RepositoryContentCounter.Types.fs`, and `src/Grace.Types/ManifestContributionWorkflow.Types.fs`. |
| HTTP routes, errors, and authorization | Implemented; operator routes remain internal | Branch producers are public; `/admin/manifest-contribution/diagnose` and `/admin/manifest-contribution/repair` require SystemAdmin and remain absent from public OpenAPI. |
| CLI and SDK | Stable Reference identity implemented; operator scripts intentionally separate | `src/Grace.CLI/Command/Branch.CLI.fs`, `src/Grace.CLI/Command/Reference.CLI.fs`, `src/Grace.SDK/Branch.SDK.fs`, and the three maintained PowerShell scripts. |
| OpenAPI and generated clients | Implemented and fresh for stable Reference identity | `src/OpenAPI/OpenAPI.ProofManifest.json`, `sdk/generated/generator-report.json`, and `sdk/generated/matrix/generator-matrix-evidence.json`. Diagnosis and repair are intentionally not applicable to public generated clients. |
| Events and background convergence | Implemented and uniform | Deterministic `Reference/{ReferenceId}/Created`, actor rereads, exact relationships, bounded counters/workflows, and type-neutral tests. |
| Durable data and retention | Implemented and retain-first | Reference and DirectoryVersion actors are authoritative; exact relationships and accounting snapshots are rebuildable; ContentBlock range state owns physical contribution. |
| Operations and documentation | Updated by MCA-09 | `docs/Manifest contribution accounting runtime.md`, `docs/Manifest contribution diagnosis.md`, `docs/Manifest contribution repair.md`, the ADR, `CONTEXT.md`, and `src/docs/ASPIRE_SETUP.md`. |
| Proof and accepted evidence | Implemented and immutable | The 18-file packet, publisher tests, this independent audit, focused .NET tests, freshness checks, and current-head CI/review gates. |

## Evidence identity and freshness

The identities have distinct meanings:

1. `60da9740236e36bfab770a1f246f0d993740b7fc` is the clean source exercised by the grouped runtime command.
1. `dce9fe0a2677459e521b30737c296f917f7eb701` is its immediate artifact-only child and contains exactly the 18 packet
   files.
1. `96c3c09300d3fbe0d65fad309afe2bc3983dd280` is the two-parent merge that admitted the artifact child to the Epic.
1. The MCA-09 commit is a fourth identity containing audit-only documentation and proof.

The audit fails if any measured publisher, runtime, fixture, production, public, durable, or generated path changes
between the measured source and the current audit head. It separately verifies that packet bytes still equal the
artifact child.

## Capability dispositions and residual risk

### Accepted risk

- **Redis compound-interruption window.** A removal can change the bounded counter, lose its short-lived Redis result,
  stop before exact-relationship convergence, be displaced in `LastCompletedChange` by a newer addition, and later be
  delivered again. Deterministic identities, retain-first ordering, prompt exact convergence, and normal retry minimize
  this window. Product V1 accepts it rather than making Redis durable or authoritative.
- Public Branch Create response-loss recovery, Reference Promote child-Rebase response-loss recovery, and automatic
  convergence of the non-atomic Branch Promote/Rebase pair remain accepted Product V1 residual risks from MCA-02.
- The DirectoryVersion physical-deletion incoming check occurs once immediately before deletion begins. A later new
  incoming relationship does not cancel cleanup; this is the accepted DEC-MCA-013 boundary.

### Deferred

- Continuous full verification, automatic or scheduled repair, production probes, dashboards, premature sharding,
  never-retained DirectoryVersion automation, success during Service Bus outage, long-handler lock-renewal proof, and
  Azure availability, cross-region, HA/DR, or SLO commitments.

### Rejected

- Foreground graph traversal or counting; Save-specific accounting; an outbox or publication lifecycle; permanent
  receipts; lifetime histories; Redis membership or source-of-truth use; durable pause, deletion-progress, or repair
  state; default outer Polly; mandatory Cosmos change feed; and migration for development-only data.

### Not applicable

- Production-data migration is not applicable because Grace has no production data to preserve.
- Public OpenAPI/SDK/generated commands for diagnosis and repair are not applicable because those are intentionally
  internal SystemAdmin workflows.
- Azure performance, partition heat, throttling, failover, HA/DR, and SLO conclusions are not applicable to the local
  emulator packet.
- A product fix for Issue #774 is not applicable because the corrected witness produced no deterministic RED.

## Operator and validation commands

Publish the fixed packet only when a new measured-source change intentionally makes the accepted packet stale:

```powershell
pwsh ./scripts/measure-manifest-contribution-accounting.ps1 `
  -OutputDirectory ./artifacts/manifest-accounting-measurements
```

Run the final audit without regenerating the packet:

```powershell
pwsh ./scripts/tests/audit-manifest-contribution-accounting.Tests.ps1
```

Diagnosis and dry-run/execute repair commands, validation, exit codes, and terminal report contracts are maintained in
[Manifest contribution diagnosis](Manifest%20contribution%20diagnosis.md) and
[Manifest contribution repair](Manifest%20contribution%20repair.md).
