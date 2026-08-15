# Current-Branch Materialization Final Audit

This artifact is the WS7.4 completion audit for [#677](https://github.com/ScottArbeit/Grace/issues/677). It records
the implementation and proof integrated through child PRs #683, #685, #698, #702, and this #681 branch. WS7.4
replaces the closed, unimplemented #505 / PR #552 path; it does not revive it.

## Scope And Status

- Parent mini-epic: [#677](https://github.com/ScottArbeit/Grace/issues/677).
- Final audit issue: [#681](https://github.com/ScottArbeit/Grace/issues/681).
- Integration branch: `epic/677-current-branch-reference-materialization-rewrite`.
- Audited base: `05e013dfe0b765e7e935fe93495de81f7609f67a` from merged #701 / PR #702.
- ADR: [ADR 0010](adr/0010-current-branch-materialization-trust-boundary.md).
- Status terms: `Implemented and proven` has committed code plus focused proof. `Deferred` is owned by a named
  follow-up. `Rejected` is intentionally excluded from WS7.4.

## Contract Propagation Map

| Surface | WS7.4 disposition | Evidence |
| ------- | ----------------- | -------- |
| Watch runtime and serialized coordinator | Implemented and proven | `Watch.CLI.fs` validates, serializes, gates, applies, and publishes one exact Reference. |
| Watch IPC and CLI JSON | Unchanged for blocked materialization | `GraceWatchStatus`, its private IPC contract, and `WatchStatusDto` are unchanged by #681. |
| Durable local state | Implemented and proven | Exact apply replaces `GraceStatus` only inside the owned update-marker boundary. |
| Apply coordinator and object cache | Implemented and proven | Exact target plan and cache preflight complete before target mutation. |
| Docs and tests | Implemented and proven | This ADR/audit and `Watch.Tests.fs` capture the final trust and publication proofs. |
| SDK, OpenAPI, server DTOs, and generated clients | N/A | WS7.4 preserves their existing public shapes; #701 audited existing strict Reference projections. |

## Decision Traceability

| ID | Binding #677 decision | Status | Current evidence |
| -- | --------------------- | ------ | ---------------- |
| D1 | Notifications carry concrete Reference and full dual-hash root identity. | Implemented and proven | `Services.currentBranchReferenceProtocolValidationDecision`; `current branch reference decision rejects missing root identity and empty reference id`. |
| D2 | BranchDto latest Reference decides staleness. | Implemented and proven | `Services.decideLatestCurrentBranchReferenceMaterialization`; latest-mismatch, delayed cross-type, and arrival-order tests in `Watch.Tests.fs`. |
| D3 | Exact References process one at a time without coalescing or reordering. | Implemented and proven | `WorkingDirectoryMaterialization.runSerializedLane`; coordinator tests keep accepted R1 ahead of later R2. |
| D4 | Materialization has no broad repository or marker-window scan. | Implemented and proven | `buildCurrentBranchRemoteMaterializationPlanForWatchTests` uses the exact remote closure and target checks; the materialization path has no `scanForDifferences` call. |
| D5 | The working tree becomes the exact Reference, including type swaps. | Implemented and proven | `applyCurrentBranchMaterializationTargets`; exact-plan tests cover file-to-directory and directory-to-file replacement. |
| D6 | Ambiguous IPC degrades/resyncs and revalidates the exact Reference. | Implemented and proven | `currentBranchMaterializationStatusGate` and coordinator degraded-resync revalidation tests. |
| D7 | Object-cache content is preflighted; apply never falls back to download. | Implemented and proven | `verifyCurrentBranchMaterializationObjectCache` runs before mutation; missing/corrupt cache tests assert no target mutation. |
| D8 | Remote materialization never creates a local Save. | Implemented and proven | The exact-apply client boundary has no Save operation; apply-owned observation suppression tests prove mutated targets do not enter local Watch work. |
| D9 | Blocked state remains internal; do not add CLI JSON, IPC DTO, or GraceWatchStatus fields. | Implemented and proven | #681 changes only coordinator/private test seams and docs. `Services.GraceWatchStatus` and `WatchCheckStatusDto` keep their existing shapes; blocked/degraded outcomes remain internal. |
| D10 | Clean IPC follows successful apply, durable update, marker closure, and final verified publication. | Implemented and proven | `processCurrentBranchMaterializationNotification` now requires `PublishCleanIpcAfterApply`; new publication-order and unproven-publication tests prove clean ordering and dirty/degraded fallback. Existing exact-apply tests cover durable replacement and marker-retirement failure. |
| D11 | A newer Reference may return to an old root only when BranchDto latest names it. | Implemented and proven | `latest current branch decision accepts newer reference that returns to older root value`. |
| D12 | No new data-source construct, root history, or superseded-root policy. | Implemented and proven | Current code uses configuration lifecycle, BranchDto refresh, and serialized coordination only; source and diff review found no new ledger or root-history state. |
| D13 | Stop before adding an unnamed materialization-domain construct. | Audited | #681 needed no new product concept or public contract. The final-publication seam is a private coordinator dependency, not a materialization data source or status model. |

## Child-Issue Completion Map

| Issue | Completion classification | Implementation and proof |
| ----- | ------------------------- | ------------------------ |
| [#678](https://github.com/ScottArbeit/Grace/issues/678) | Implemented and proven | Strict notification identity, overall/latest BranchDto authority, stale drop, and legitimate old-root return proof. |
| [#679](https://github.com/ScottArbeit/Grace/issues/679) | Implemented and proven | Serialized lane, post-lease target revalidation, dirty/pending gate, durable local-state gate, and degraded resync. |
| [#680](https://github.com/ScottArbeit/Grace/issues/680) | Implemented and proven | Exact-target apply, marker ownership, object-cache preflight, type swaps, no broad scan, and apply-owned observation suppression. |
| [#701](https://github.com/ScottArbeit/Grace/issues/701) | Implemented and proven | Merged PR #702 makes current version identity strict for SHA-256 and BLAKE3 and preserves the narrow typed Reference sentinel contract. |
| [#681](https://github.com/ScottArbeit/Grace/issues/681) | Implemented and proven | Final verified-clean publication gate, dirty reassertion on unproven final publication, ADR, and this traceability audit. |

## Explicit Deferrals And Rejections

- [#506](https://github.com/ScottArbeit/Grace/issues/506) owns startup/reconnect catch-up and recovery after
  process-local pending work is lost. It must consume this BranchDto-confirmed materialization boundary.
- WS6 owns normalized observation hardening and operability. It must not redefine the WS7.4 authority or publication
  rules.
- PR #552 is rejected as an implementation path. It is historical coordination evidence only.
- WS7.4 has no public blocked-materialization status field, no notification-payload persistence, no broad-scan proof,
  no missing-object apply fallback, and no root-history or superseded-root ledger.

## Publication Side-Effect Audit

The final publication sequence is deliberate:

1. The coordinator publishes and verifies dirty/pending IPC before it calls the exact apply boundary.
2. Exact apply preflights metadata, targets, and object cache; it mutates targets under its owned update marker.
3. Exact apply persists replacement `GraceStatus` and closes the owned marker before returning success.
4. Only then does the coordinator clear its internal pending marker and request verified clean IPC publication.
5. An unproven final clean publication restores pending state, reasserts dirty IPC, requests degraded/resync, and returns
   `WaitingForDegradedResync`, not `Applied`.

The focused test `current branch materialization coordinator publishes clean only after successful apply completion`
records durable-state and marker closure before clean publication. The paired unproven-publication test records dirty
reassertion and degraded/resync rather than an applied clean result. Existing failed-apply coverage confirms a partial
apply remains dirty and unusable.

## Validation Record

- Targeted Fantomas passed for `Watch.CLI.fs` and `Watch.Tests.fs`.
- Focused `CurrentBranchMaterializationPublication` passed 2/2.
- Focused `CurrentBranchMaterializationCoordinator` passed 36/36, including blocked, degraded, failed-apply, and
  final-publication ordering cases.
- MarkdownLint passed for this audit and ADR 0010 with the repository-local 120-column `MD013` limit.
- `git diff --check` passed before final handoff.
- The required `pwsh ./scripts/validate.ps1 -Full` gate was run twice because the first local transport detached after
  build/test startup. The permitted clean-state retry visibly passed build with 0 warnings and 0 errors, Types 140/140,
  Authorization 53/53, and Server.Unit 738/738, then started and cleaned up the Aspire-backed Server fixture. The
  command transport detached before either Full run returned the final Server/CLI totals or exit code. This audit does
  not claim a local Full pass; the orchestrator should use the PR's authoritative Full run or rerun the gate where its
  exit status can be retained.

This audit is committed with the code so the final PR can retain the decision map independently of issue-comment
history.

## Coordination Finding

Issue #681 requests `docs/adr/0007-current-branch-materialization-trust-boundary.md`, but the integration branch
already contains `docs/adr/0007-empty-directories-are-versioned-structure.md`, plus ADRs 0008 and 0009. This worker
uses the next available number, ADR 0010. The orchestrator should patch #681's ADR path before closing the issue.
