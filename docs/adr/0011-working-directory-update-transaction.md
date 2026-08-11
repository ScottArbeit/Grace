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

The current `WorkingDirectoryMaterialization` module serializes arbitrary callbacks but does not own planning, marker
behavior, filesystem mutation, content verification, durable local state, finalization, or failure classification.
Branch and Watch each implement substantial private mutation workflows, while Connect extracts a server zip directly
into working and object paths without the same lease or marker contract.

These paths can change the same local directory and SQLite state. Their differences are real—Watch advances an ordered
cursor, Branch publishes selected branch identity, and Connect preserves zip retrieval—but those differences do not
justify three local-integrity implementations.

## Decision

- The internal module exposes one generic `run` operation and one `retryFinalization` operation. Caller-specific
  constructors create private normalized requests.
- Target identity contains repository, branch, root DirectoryVersion, SHA-256, and BLAKE3. Caller operation identity is
  separate and deterministic. Each execution attempt receives a separate random marker token.
- Prepared-content adapters expose exact immutable manifests and readable uncompressed bytes. They never provide
  mutation plans or database callbacks.
- A repository ID plus normalized local-root-path hash scopes the exclusive file lease, versioned marker, and completion
  sidecar. Branch identity is excluded from the physical scope.
- The module rereads selected target and local state after acquiring the lease, builds a fresh plan, mutates only proven
  paths, verifies object and working bytes with SHA-256 and BLAKE3, and proves the final selected root.
- SQLite local completion is the irreversible point. One transaction records matching status, required object-cache
  metadata, Connect's initial cursor when present, and a bounded update completion row.
- SQLite stores no running operation. It retains the latest terminal row per caller and one unresolved pending
  finalization.
- The completion sidecar is derived Watch notification evidence. It cannot override SQLite completion truth.
- Branch and Watch finalization is idempotent and runs after local completion while the lease remains held. A failed
  finalization blocks every different update in that local scope.
- The five outcomes are `Unchanged`, `Updated`, `Rejected`, `UpdateIncomplete`, and `FinalizationIncomplete`.
  Incomplete outcomes return nonzero. `FinalizationIncomplete` says the working directory was updated and recommends
  `grace doctor --repair-local-state`.
- The same deterministic operation may adopt a known orphaned marker only after acquiring the lease and performing
  complete revalidation and replanning. Different or unrecognized markers require Doctor.
- Doctor first attempts a filesystem-free retry of recorded finalization, then may use exact local-state reconstruction.
  Doctor never rolls back or guesses working-directory content.
- Connect keeps its zip download. It stages and validates the zip before lease acquisition, performs no network reads
  under the lease, verifies objects before populating the working directory, and limits `--force` to conflicting target
  paths.

The complete requirements, state model, propagation map, and proof contract are in
[Working Directory Update](../Working%20Directory%20Update.md).

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
