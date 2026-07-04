---
status: accepted
date: 2026-07-04
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Refresh Watch SignalR subscriptions at branch transition

Grace Watch will refresh branch-scoped SignalR subscription state at the same-process branch transition boundary. When
Watch observes that another Grace command has completed a Grace-owned branch switch, Watch reloads the current
repository configuration, retargets branch-scoped local coordination, recalculates the current branch's parent branch,
and re-registers the live SignalR connection for that parent before trusting parent-promotion events again.

## Context

`grace watch` keeps a foreground SignalR connection open so Grace Server can notify it about repository events and
parent-branch promotions. Parent promotions are the trigger for Watch auto-rebase: if the promotion event targets the
parent branch Watch currently trusts, Watch reads local status and runs the rebase path for the current branch.

Same-process branch switches are different from a fresh `grace watch` start. The process, timers, SignalR connection,
and event handlers continue running while the persisted repository configuration changes from branch A to branch B.
Without an explicit refresh, the connection and local parent predicate can still reflect branch A's parent after Watch
has adopted branch B. That creates two incorrect outcomes:

- a promotion of branch B's parent may not trigger the intended Watch auto-rebase path;
- a promotion of branch A's old parent can still satisfy a stale predicate and run auto-rebase under branch B's current
  configuration.

The transition boundary is already the point where Watch retires the previous branch IPC, reloads `Current()`, retargets
the update-marker watcher, reloads `GraceStatus`, and decides whether healthy incremental work can resume. That makes
it the nearest serialized boundary that knows the old branch identity has been abandoned and the new branch identity is
authoritative.

## Decision

The same-process branch transition boundary owns SignalR branch subscription refresh.

When Watch completes a trusted branch transition:

- it clears the locally trusted SignalR branch/parent identity before recalculating the new parent;
- it re-registers the live SignalR connection with `RegisterParentBranch(currentBranchId, currentParentBranchId)`;
- it replaces the local auto-rebase predicate only after the new parent registration path succeeds;
- it rejects promotion events for the old parent after the branch identity has moved to the new branch;
- it allows promotion events for the new parent to reach the existing auto-rebase path after transition completion.

Repository-wide SignalR registration remains outside the refresh work because the current server contract registers that
group by `RepositoryId` only. A branch switch does not change repository identity, so `RegisterRepository(repositoryId)`
is branch-independent for WS5.5. If a future server contract adds branch-sensitive repository subscriptions, that change
must extend this transition-boundary refresh rather than relying on startup-only registration.

## Consequences

This keeps Watch's parent-promotion trust aligned with the same branch identity used by local config, IPC, marker
watching, and `GraceStatus`. The fix is intentionally local to the transition boundary and SignalR predicate; it does
not introduce a broad branch-transition domain construct.

The server's current parent-branch registration API is additive and does not expose an unregister operation. The client
therefore refreshes by registering the new parent and replacing its local predicate so any stale old-parent delivery is
ignored. A future server-side unsubscribe or connection restart can tighten group membership, but it is not required for
the WS5.5 invariant because stale old-parent events no longer satisfy Watch's auto-rebase predicate.

## Outside WS5.5

This ADR does not define remote sync materialization, durable journal authority, new `--force` or `--wait` behavior,
or a `grace watch flush` command. Those surfaces belong to other workstreams. It also does not change SDK, OpenAPI, or
public CLI output contracts.

## Rejected alternatives

### Refresh only on Watch startup

Startup registration is insufficient for a same-process branch switch. The SignalR connection and handler closures can
outlive the original branch identity, so startup-only registration leaves stale parent state in a running process.

### Run auto-rebase based only on the event target

Trusting any promotion event that reaches the client would make server group membership the only authority. Watch must
also enforce the current branch's parent identity locally so additive or delayed SignalR delivery cannot trigger a
stale rebase.

### Move refresh into the rebase handler

The rebase handler runs after an event has already passed the parent predicate. Refreshing there is too late to prevent
old-parent events from satisfying the stale predicate. The transition boundary must refresh before Watch resumes
trusting parent-promotion events.
