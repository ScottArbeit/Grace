---
status: accepted
date: 2026-08-12
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use one Branch-derived Working Directory Update transaction

Grace will first route `grace switch` through one deep, private Branch-derived `WorkingDirectoryUpdate` transaction.
The module owns local ordering, verification, completion, marker handling, and Branch finalization. Watch and Connect
are deferred callers; they do not justify a generic request model before their own work is ready.

## Context

The earlier all-caller planning and PR #854 combined typed selection persistence, filesystem planning, finalization,
Save admission, output, and phase proof in one implementation slice. Exact-head review showed that a broad private
request made core Branch rules implicit: typed marker evidence could be lost, Reference finalization could not repeat,
planning could use a pre-Save graph, path topology was incomplete, and cancellation was detached from the public action.

The replacement sequence needs one real Branch path with a small input and explicit durable facts. Hash selectors remain
supported: they select an exact root without a Reference and keep the current Branch active.

## Decision

- Immediately after successful Save or no-Save admission, Branch constructs an immutable `AcceptedBranchPhase` holding
  the accepted SQLite revision, canonical complete-status fingerprint, and public action token. It holds no status graph,
  alternate path, selected-state reader, or mutable callback.
- The first private `WorkingDirectoryUpdate.run` input is Branch-only. Its caller provides the unchanged
  `AcceptedBranchPhase`, typed Branch selection, exact target graph, verified prepared content, and diagnostic
  correlation.
- The module derives canonical configuration, working/object/SQLite paths, scan input, operation identity, completion
  facts, marker disposition, and typed Branch finalization facts. It rereads current revision and complete status under
  the lease, then compares them with the immutable phase. It does not accept an arbitrary path or context bag, old
  status graph, selected-state reader, finalizer callback, mutation plan, or database handle.
- Branch selection is typed. `Reference` carries the required selected Reference ID. Hash-selected `DirectoryVersion`
  carries no Reference ID, binds the operation to its exact selected root, and retains the current Branch as both
  previous and selected Branch.
- A successful current-version Save produces the only accepted local baseline for a Save-enabled switch: its SQLite
  revision plus a canonical fingerprint of the complete status graph. The same phase carries that binding through target
  resolution and preparation. After acquiring the lease, the module rereads and requires it before planning.
- The module inspects and cleans markers through explicit dispositions. Missing or exact-cleaned evidence may advance
  the relevant phase. Different-operation, malformed or unsupported, unreadable, and exact-cleanup-failed evidence is
  preserved and never converted into success.
- The module plans the complete tracked topology, including empty directories and file/directory transitions; it
  preserves ignored content. It verifies dual-hash objects before use and independently verifies the complete final
  graph and both root hashes before local completion.
- The public action token in the phase applies through admission, Save, resolution, preparation, lease waiting, object
  publication, and the final pre-mutation check. It is deferred only after working-tree mutation starts.
- SQLite local completion remains the irreversible local point. `Reference` finalization occurs afterward while the
  lease is held: previous Branch applies once, exact selected Branch is already applied, and any third Branch rejects.
  `DirectoryVersion` finalization proves the current Branch remains active and changes no Branch identity.
- The existing five outcomes remain unchanged. A post-local-completion cleanup or finalization failure is
  `FinalizationIncomplete`, is nonzero, says that bytes were updated, and recommends
  `grace doctor --repair-local-state`.
- Real filesystem and SQLite tests prove phase and recovery behavior. Bounded built-command tests prove selector routing
  and public output. Every failure seam is deterministically activated by one of those proof boundaries.

The complete requirement ownership, phase table, and proof contract are in
[Working Directory Update](../Working%20Directory%20Update.md).

## Consequences

The module becomes deep without becoming generic. Branch callers are small and cannot assemble contradictory local
facts, while the module owns the current Branch transaction end to end. Issue #869 persists typed facts and marker
evidence; #870 delivers the hash-selected tracer; #871 adds repeatable Reference finalization; #872 adds Save-enabled
baseline binding. Issue #842 later uses the same exact pending Branch facts for Doctor recovery without working-file
mutation.

The phase is executable rather than descriptive: #870 proves deterministic no-Save construction and phase consumption;
issue #872 proves Save-enabled construction and a tracked edit during preparation that the post-lease comparison rejects
before target mutation. This preserves the one deep module seam without allowing an arbitrary caller status graph.

Watch and Connect stay outside this interface until a later accepted design names their independent admission and
finalization rules. This avoids treating one future caller's callback or path needs as a current Branch contract.

Product V1 still excludes rollback, durable per-file journals, automatic general recovery, migration, compatibility,
and network access under the lease. Local schema replacement remains allowed because Grace has no production local-data
compatibility obligation.

## Rejected alternatives

### Restore the generic all-caller request

An interface that permits callers to supply alternate local paths, prior status, selected-state reading, or finalization
returns the trust boundary to callers. It recreates the shape superseded after PR #854 rather than solving its review
findings.

### Treat hash selection as a Reference

A selected root can have no Reference or more than one Reference. Inventing one would change Branch identity semantics
and make persisted recovery facts false. `DirectoryVersion` keeps the exact root and current Branch instead.

### Plan from the pre-Save status

After a successful Save, the earlier graph no longer names the accepted current version. Using it would make the
subsequent plan stale by construction.

### Collapse marker evidence to cleanup succeeded or failed

Different, malformed, unsupported, unreadable, and exact-cleanup-failed evidence have distinct recovery implications.
A Boolean cannot preserve enough information to make finalization or Doctor behavior truthful.

## Related decisions

- [ADR 0009](0009-refresh-watch-signalr-subscriptions-at-branch-transition.md) remains the accepted Watch refresh
  decision; Watch integration is deferred from this Branch transaction.
- [ADR 0010](0010-current-branch-materialization-trust-boundary.md) remains the accepted Watch replay boundary; it does
  not create a Watch request constructor in the current Branch slice.
