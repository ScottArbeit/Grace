---
status: accepted
date: 2026-07-03
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Gate Grace Watch reconciliation by runtime state

Grace Watch will use explicit runtime state to decide when filesystem observations may update local status and branch
history. Healthy incremental mode can apply event-derived observations. Startup and resync modes may capture and queue
observations, but normal event-derived status application waits until Watch has restored a trusted status boundary.
Suspended and stopping modes do not capture new incremental observations.

## Context

`grace watch` is becoming an incremental, durable, recoverable workflow for clean current-branch sync. File system
events are useful because they avoid full scans after every local edit, but they are not a durable source of truth by
themselves. Watch can lose confidence when the `FileSystemWatcher` reports an overflow, when a scan fails before the
durable status boundary, or when pending work was captured under an older branch, root, path, or observation identity.

Before this decision, a happy-path implementation could accidentally continue applying queued observations after
confidence loss. That would make Watch look fast while allowing stale local events to create saves, update
`GraceStatus`, or advertise an IPC snapshot that other commands and agents should not trust.

Grace is pre-production, so this decision does not preserve compatibility with old local data. The important
compatibility surface is the current command and IPC behavior: older readers can ignore compact status fields, while
newer status readers can inspect runtime mode and safety flags before trusting incremental shortcuts.

## Decision

Grace Watch reconciliation is state-gated:

- `StartingUp` may perform startup scans and capture observations, but it does not advertise incremental safety until
  the initial trusted status snapshot is published.
- `HealthyIncremental` may capture and apply filesystem observations through the incremental status path.
- `Resynchronizing` may run scan-derived recovery and capture observations for later processing, but it defers normal
  event-derived status application until scan-derived reconciliation reaches the durable local-status boundary.
- `Suspended` does not capture or apply new incremental observations. It is used after recovery cannot safely restore
  confidence.
- `Stopping` is not a live incremental source and must not be treated as usable status.

When Watch loses confidence, it quarantines already queued observations, requests an explicit resync, and publishes a
non-incremental IPC status until recovery succeeds. Other commands and agents should treat
`CanUseIncrementalStatus = false` or `requiresExplicitResync` as a stop sign for incremental shortcuts.

`grace watch --check` is the human and machine-readable surface for this state. Human output explains starting, stale,
resynchronizing, suspended, and stopping states without local IPC paths or exception stacks. JSON output uses the
normal Grace result envelope and returns a `WatchStatusDto` with `IsRunning`, `CanUseIncrementalStatus`, `Mode`,
`Reason`, `Message`, `SafetyFlags`, freshness, and root snapshot facts.

## Consequences

This favors correctness over opportunistic event replay. Some recoverable failures may defer or suspend incremental
work even when individual queued events later turn out to be harmless. That cost is acceptable because a later explicit
resync can rebuild trusted state, while an unsafe save would pollute local status and branch history.

The runtime state model also gives agents a stable way to decide whether Watch can be used as an incremental source.
They can parse `watch --check --output Json`, use the process exit code for the coarse check result, and inspect
`Mode`, `Reason`, and `SafetyFlags` for the recovery state.

## Rejected alternatives

### Continue applying queued events after confidence loss

This keeps Watch responsive in the happy path, but it allows stale observations to cross the durable boundary. Grace
needs the local status file and saved references to reflect trusted state, not merely recently observed events.

### Treat the IPC file as a live state authority

The IPC file is a liveness and shortcut signal, not a durable authority. It can be stale after a killed process and can
be incomplete while Watch starts or recovers. Commands must derive trust from freshness, root snapshot facts, directory
index facts, runtime mode, and safety flags.

### Hide recovery states from users and agents

Returning only "running" or "not running" makes automation brittle and leaves users guessing why incremental shortcuts
are unavailable. Exposing compact mode and reason fields lets humans and agents make the same conservative decision.
