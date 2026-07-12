---
status: accepted
date: 2026-07-11
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Trust current-branch materialization at the BranchDto boundary

Grace Watch materializes a same-branch remote Reference only through a small, serialized trust boundary. The boundary
keeps local work safe while allowing a server-confirmed Reference to make the working tree match its exact contents.

## Context

SignalR delivery is useful for promptly waking Watch, but delivery order and process-local payloads cannot establish
which Reference is current. Watch also cannot safely overwrite local work when IPC, local durable state, or the
object cache is incomplete. Earlier implementation attempts showed that treating a notification, an old root, or a
local scan as authority creates unsafe materialization policy.

## Decision

- Notifications are triggers. A materializable notification carries a concrete `ReferenceId` and complete dual-hash
  root identity, then Watch refreshes `BranchDto` before it applies anything.
- `BranchDto` latest Reference is the server-side authority. A notification whose concrete id does not match is stale;
  a newer Reference may legitimately return to an older root when the latest `BranchDto` names that Reference.
- Same-branch materialization is one-at-a-time. Each exact Reference owns the serialized lane and is revalidated at
  the defined safe points; arrival order does not select, merge, or replace work.
- Materialization uses exact Reference targets. It does not broad-scan the repository, maintain root-history policy,
  or download missing objects while applying. Required cached objects are preflighted before mutation.
- Missing, stale, unreadable, blocked, or ambiguous IPC/local-state evidence is degraded or resync behavior. Watch
  preserves the exact Reference for revalidation instead of treating uncertainty as clean or as a permanent stale
  result.
- Remote materialization creates no local Save. Its Grace-owned marker suppresses apply-owned observations while the
  exact target plan mutates the working tree.
- Watch publishes a clean IPC snapshot only after the exact apply succeeds, durable `GraceStatus` is updated, the
  materialization update marker is closed, and the final clean snapshot is verified. If that final verification fails,
  Watch reasserts dirty IPC and requests degraded/resync behavior rather than reporting an applied clean state.

## Consequences

This favors a conservative retry over a potentially clean but unproven state. A delayed or invalid notification can
cost an additional BranchDto lookup or explicit resync, but it cannot make arrival order, stale roots, or incomplete
local evidence authoritative. Startup/reconnect catch-up and durable recovery of process-local pending work remain
the separate WS7.5 scope.

## Rejected alternatives

### Use SignalR payloads as durable latest authority

SignalR can be delayed, duplicated, or reordered. A notification without a matching current `BranchDto` is not safe
materialization work.

### Broad-scan or record root history before applying

Whole-repository scans and root-history ledgers add a second authority model. Exact target checks and the concrete
Reference already provide the required boundary.

### Publish clean before final verification

Other Grace commands may trust a clean IPC snapshot. Publishing it before durable apply and marker closure, or when
the final write cannot be verified, would let an unproven state cross that process boundary.
