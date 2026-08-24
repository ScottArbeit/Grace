---
status: accepted
date: 2026-08-11
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use one verified Working Directory Update transaction

Grace will route Branch switching, Watch current-Reference replay, and Connect retrieval through one deep internal
`WorkingDirectoryUpdate` module. The module owns the ordering-sensitive local transaction while each caller retains
admission, target selection, remote retrieval, scheduling, and presentation.

## Context

The legacy `WorkingDirectoryMaterialization` module serializes arbitrary callbacks but does not own planning, marker
behavior, filesystem mutation, content verification, durable local state, finalization, or failure classification.
Branch and Watch now route those effects through Working Directory Update, while Connect still extracts a server zip
directly into working and object paths without the same lease or marker contract.

These paths can change the same local directory and SQLite state. Their differences are real—Watch advances an ordered
cursor, Branch publishes selected branch identity, and Connect preserves zip retrieval—but those differences do not
justify three local-integrity implementations.

## Decision

- The internal module exposes one exact five-input Branch `run` operation—sealed accepted phase, typed selection,
  exact target graph, immutable prepared content, and diagnostic correlation—and a narrow Watch replay adapter over the
  same local-application stage. Persisted-facts-only finalization reconstructs either caller. Callers cannot supply
  alternate paths, status graphs, writers, finalizers, or generic request bags.
- Target identity contains repository, branch, root DirectoryVersion, SHA-256, and BLAKE3. Caller operation identity is
  separate and deterministic. Each execution attempt receives a separate random marker token.
- Prepared-content adapters expose exact immutable manifests and readable uncompressed bytes. They never provide
  mutation plans or database callbacks.
- A repository ID plus normalized local-root-path hash scopes the exclusive file lease, versioned marker, and completion
  sidecar. Branch identity is excluded from the physical scope.
- The module rereads selected target and local state after acquiring the lease, builds a fresh relevant-topology plan,
  mutates only proven paths, verifies object and working bytes with SHA-256 and BLAKE3, and proves the final relevant
  selected root. Unrelated ignored or untracked content remains preserved and excluded unless it is a destructive
  required-path, case-alias, or replaced-subtree collision.
- SQLite local completion is the irreversible point. One transaction records matching status, required object-cache
  metadata, Connect's initial cursor when present, and a bounded update completion row. `DirectoryVersion` selection
  records terminal completion in that transaction; `Reference` selection records pending completion for later Branch
  publication or Watch verification.
- SQLite stores no running operation. It retains the latest terminal row per caller and one unresolved pending
  finalization.
- The completion sidecar is derived Watch notification evidence. It cannot override SQLite completion truth.
- Branch and Watch finalization is idempotent and runs after local completion while the lease remains held. A failed
  finalization blocks every different update in that local scope.
- The five outcomes are `Unchanged`, `Updated`, `Rejected`, `UpdateIncomplete`, and `FinalizationIncomplete`.
  Incomplete outcomes return nonzero. `FinalizationIncomplete` says the working directory was updated and recommends
  `grace doctor --repair-local-state`.
- The same deterministic operation may adopt a known orphaned marker only after acquiring the lease and performing
  complete revalidation and replanning. Exact adoption reconciles each requirement as `NeedsApply` or
  `AlreadySatisfied` from real dual-hash evidence; mixed partial progress is not rewritten. Different or unrecognized
  markers require Doctor, except for one exact terminal-owned residue case. A later operation may replace that residue
  only when SQLite matches the marker operation and target and current status still names the same target root.
- Doctor first attempts a filesystem-free retry of recorded finalization, then may use exact local-state reconstruction.
  Doctor never rolls back or guesses working-directory content.
- Connect keeps its zip download. It stages and validates the zip before lease acquisition, performs no network reads
  under the lease, verifies objects before populating the working directory, and limits `--force` to conflicting target
  paths.

## Serial delivery and test boundary

Issue #960 supplied the first public tracer across topology composition, held-lease application, `VerifiedLocalRoot`,
terminal SQLite completion, marker cleanup, and `grace branch switch --sha256-hash` or `--blake3-hash`. Issues #922,
Issue #923, Issue #871, and Issue #872 completed shared local application and Branch Reference completion. Issue #843 now consumes those
states for ordered Watch replay. Issue #900 and Issue #901 remain superseded by Issue #960.

Remote hash resolution, target retrieval, download, and immutable preparation hold none of the Branch workflow,
legacy materialization, or WDU leases. Only the WDU transaction holds its local lease for fresh reread, mutation,
`VerifiedLocalRoot`, SQLite completion, and terminal outcome. Cancellation controls through the transition into
`VerifiedLocalRoot`, including zero-action reconciliation; it is non-controlling during SQLite completion. Markers and
sidecars are evidence, not leases.

SQLite terminal completion is decisive durable truth. Marker and sidecar evidence cannot manufacture, downgrade, or
replace it. For DirectoryVersion, the verified-status transaction also records terminal completion. Exact marker
cleanup follows that transaction and cannot downgrade success. Exact replay returns `Unchanged`; safe replacement of
terminal-owned marker residue uses the matching SQLite operation, target, and status root. Runtime tests use the
five-input operation with real filesystem and SQLite facts. Ephemeral `bytesChanged` selects distinct changed and
unchanged terminal rows; Branch and Watch Reference operations remain on their ordinary post-completion rows without
inferring that DirectoryVersion-only discriminator.

The complete requirements, state model, propagation map, and proof contract are in
[Working Directory Update](../Working%20Directory%20Update.md).

## Lifecycle projection

The projection below is an existing contextual consumer, not proof that the replacement packet has been published.
Issue #929 rewrites and compares all fifteen exact projection blocks from the compiler result after PR #930 merges.

<!-- grace:wdu-lifecycle-projection:adr-0011:start -->
```json
{"schema":"grace.wdu.lifecycle-projection/v2","artifact":"adr-0011","canonical":"docs/Working Directory Update.md#normative-branch-lifecycle-table","canonicalContentDigest":"ccd29ba6b55dde396be5c1c9244958999e7cc8173c0e5e8243f1b57fe4bc3c92","assignmentDigest":"fb20589eaffb4c7a7c8db2b1c215fe751bef3d41145fef925f07d7648f501053","rowCount":66,"applicabilityKeyCount":244,"requirementCount":19,"artifactCount":15,"requirements":[{"id":"REQ-001","owner":"#960"},{"id":"REQ-002","owner":"#869"},{"id":"REQ-003","owner":"#837"},{"id":"REQ-004","owner":"#839"},{"id":"REQ-005","owner":"#869"},{"id":"REQ-006","owner":"#898"},{"id":"REQ-007","owner":"#960"},{"id":"REQ-008","owner":"#960"},{"id":"REQ-009","owner":"#838"},{"id":"REQ-010","owner":"#838"},{"id":"REQ-011","owner":"#871"},{"id":"REQ-012","owner":"#871"},{"id":"REQ-013","owner":"#960"},{"id":"REQ-014","owner":"#960"},{"id":"REQ-015","owner":"#842"},{"id":"REQ-016","owner":"#871"},{"id":"REQ-017","owner":"#846"},{"id":"REQ-018","owner":"#928"},{"id":"REQ-019","owner":"#960"}],"artifactIds":["adr-0011","epic-835","issue-842","issue-843","issue-846","issue-869","issue-898","issue-928","issue-960","issue-922","issue-923","issue-900","issue-901","issue-871","issue-872"],"assignment":{"rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-008","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-023","WDU-LC-025","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-033","WDU-LC-035","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-003"]}}
```
<!-- grace:wdu-lifecycle-projection:adr-0011:end -->

## Consequences

The module becomes deep: a small typed interface hides cross-process serialization, stale-state rejection, fresh
planning, marker ownership, object publication, working-directory mutation, verification, SQLite commit, cleanup,
finalization, cancellation, and outcome classification.

Callers remain locally understandable because their policy stays outside the module. A new prepared content form can
reuse the local transaction, but a new caller kind or finalization rule requires an explicit design decision.

Product V1 recovery is deliberate rather than comprehensive. Exact same-operation retry and Doctor recovery are
supported; rollback, durable per-file journals, automatic general recovery, hostile-local-process defense, and
multi-host coordination are not.

The local SQLite schema can be replaced without migration because Grace has no production local data contract. Public
CLI output changes with implementation, while server DTOs, HTTP routes, SDK facades, OpenAPI, and generated clients are
expected to remain unchanged.

## Rejected alternatives

### Keep the callback-only serialization wrapper

A lease around arbitrary caller callbacks leaves the critical plan, mutation, verification, commit, and recovery order
duplicated and unprovable as one contract.

### Expose separate Branch, Watch, and Connect transaction operations

Caller-oriented entries are ergonomic but make today's caller taxonomy the architecture. Private request constructors
provide the same ergonomics without three transaction implementations.

### Expose generic transaction participants

Pluggable local commits, SQL callbacks, or caller-supplied mutation plans would move the hardest invariants back into
callers and make the module shallow.

### Use only the completion sidecar

A sidecar written after status commit leaves an uncertain crash gap and cannot atomically identify the completed
operation. SQLite completion closes that gap; the sidecar remains notification evidence only.

### Hold the lease while reading the remote zip

Remote reads can stall, retry, or outlive signed access. Local staging keeps the mutation phase bounded to local
filesystem and SQLite work.

### Add rollback or a durable per-file journal

Those capabilities add inverse planning, restart state, compaction, abandonment, and recovery policy beyond the Product
V1 contract. Exact retry and explicit Doctor recovery provide the selected value without that machinery.

## Related decisions

- [ADR 0009](0009-refresh-watch-signalr-subscriptions-at-branch-transition.md) defines Watch subscription refresh after
  Branch finalization.
- [ADR 0010](0010-current-branch-materialization-trust-boundary.md) defines Watch's ordered current-Reference replay and
  cursor rules, which become caller policy around this shared local transaction.
