---
status: accepted
date: 2026-07-11
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Trust current-branch materialization at the durable event-cursor boundary

Grace Watch materializes a same-branch remote Reference only through a small, serialized trust boundary. The boundary
keeps local work safe while allowing a server-confirmed Reference to make the working tree match its exact contents.

## Context

SignalR delivery is useful for promptly waking Watch, but delivery order and process-local payloads cannot establish
which Reference work is new for this local directory. A `BranchDto` latest-reference summary has the same limitation:
it describes the branch, not which ordered events this Watch process has already accepted. Watch also cannot safely
overwrite local work when IPC, local durable state, or the object cache is incomplete.

## Decision

- SignalR notifications are wake-only. Startup, reconnect, branch transition, and a later safe local point also wake
  the same typed replay operation.
- The server's immutable Branch event snapshot owns order. Watch requests only events after the opaque cursor stored
  for the exact local repository and branch; it does not parse, increment, or compare cursor positions.
- Same-branch materialization is one-at-a-time. Eligible Commit, Checkpoint, and Save events cross the serialized lane
  in server order and revalidate current persisted identity and local safe state before mutation.
- Materialization uses exact Reference targets. It does not broad-scan the repository, maintain root-history policy,
  or download missing objects while applying. Required cached objects are preflighted before mutation.
- Missing, stale, unreadable, blocked, or ambiguous IPC/local-state evidence leaves the current event cursor
  unacknowledged. Watch fails closed; only explicit exact `grace doctor --repair-local-state` maintenance may reconstruct
  missing status and its matching boundary, and it does not fall back to `BranchDto`.
- Remote materialization creates no local Save. Its Grace-owned marker suppresses apply-owned observations while the
  exact target plan mutates the working tree.
- Watch publishes a clean IPC snapshot only after the exact apply succeeds, durable `GraceStatus` is updated, the
  materialization update marker is closed, and the final clean snapshot is verified. It persists the matching event
  cursor only after successful materialization or a verified same-root acknowledgement.
- Watch persists the response's scanned-through cursor only after every earlier eligible event in that exact response
  is terminally acknowledged. A persistence failure leaves the event replayable and idempotent.

## Consequences

This favors safe replay over a potentially clean but unproven state. Duplicate SignalR, startup, and reconnect wakes
converge through the durable cursor. An empty replay creates no coordinator work or pending transition, while an
ineligible-only interval can still close through the server-declared scanned-through cursor.

## Rejected alternatives

### Use SignalR payloads or BranchDto as durable latest authority

SignalR can be delayed, duplicated, or reordered. `BranchDto.LatestReference` is a summary and cannot prove that its
root is new to one local directory. Neither surface is safe ordering or replay state.

### Broad-scan or record root history before applying

Whole-repository scans and root-history ledgers add a second authority model. Exact target checks and the concrete
Reference already provide the required boundary.

### Publish clean before final verification

Other Grace commands may trust a clean IPC snapshot. Publishing it before durable apply and marker closure, or when
the final write cannot be verified, would let an unproven state cross that process boundary.

## Related decision

[ADR 0011](0011-working-directory-update-transaction.md) records the accepted design for a future shared Working
Directory Update transaction covering filesystem planning, mutation, dual-hash verification, marker behavior, and local
completion. That transaction is not executable until its implementation issues land; current source still uses the
callback-only wrapper and caller-owned mutation paths. This ADR continues to define Watch's server-event ordering,
replay admission, cursor progression, IPC publication, and resync policy around that future shared transaction.
