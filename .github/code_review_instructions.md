# Grace Code Review Instructions

This is the review rubric for Grace pull requests, for human and automated reviewers. The linked issue's goal and "Done
when" checks say what the change must do; this file says how to judge it. Review in three steps, in order, in one
pass.

## 1. Evidence

For each "Done when" item, the PR shows a result on the current head. Also check that:

- each new or changed test was shown red without the change and green with it (red-on-revert);
- a user-visible CLI, HTTP route, or SDK change includes a demo: the real command and its output;
- the proof matches the kind of change (the Verification table in `CONTRIBUTING.md`);
- Fantomas (F#) and markdownlint (Markdown) ran on touched files, since `Validate` runs neither.

Missing, stale, or unconvincing evidence is the first finding. Spot-check doubtful evidence; you don't need to rerun
everything.

## 2. What can go

Ask what in the diff could be deleted while every "Done when" check still passes. A new abstraction, type, option,
setting, fallback, retry, or defensive branch needs a "Done when" item or a defect scenario (below) that requires it;
without one, recommend removing it. Flag scope too: half-wired flags, fields, routes, or timers, and work the issue
left out. An accepted input is implemented, rejected, or documented as informational.

## 3. Defects

A defect finding needs all of:

1. **A realistic trigger:** a supported client, workflow, dependency behavior, ordinary retry, restart, redelivery,
   cancellation, or normal operator action that produces the problem. Technical reachability is not enough.
2. **A concrete sequence:** the shortest path from that trigger to the failure.
3. **A real impact:** wrong state, wrong result, a security or isolation failure, lost data, or a test that proves
   nothing.
4. **Proof:** a failing test or reproduction, or static evidence when the code establishes the defect directly. Without
   one, it's a note, not a finding.

Skip scenarios that need deliberate protocol misuse, hand-corrupted state, an unsupported caller, or a chain of
unlikely independent failures. One exception: an uncommon but plausible trigger still counts when it would cause an
authorization or isolation failure, unrecoverable data loss, or an irreversible external effect. Say so when you use it.

Where defects hide in Grace:

- **Types and data shape:** states that should be impossible, two stored facts where one could be derived, values that
  must change together matched up by position, booleans or nulls standing in for real cases, wire or persisted shapes
  that don't match the in-memory change.
- **Authority and ordering:** state not re-read after a wait before a consequential write, materialization, SAS
  issuance, or publication; content materialized or a resource revealed before authorization; retries, replays, and
  partial failures that aren't safe.
- **Contract propagation:** surfaces in the `src/AGENTS.md` chain that a contract change reaches but the diff misses.
- **Tests:** tests that observe an intermediate state instead of the outcome, wait with sleeps instead of deterministic
  signals, or build states no supported producer can create.
- **F#:** new `async { }` where `task { }` belongs; actor calls that drop `CorrelationId` (use `RequestContext.Set` and
  the `CreateActorProxy` helpers rather than raw `IGrainFactory.GetGrain`).

Grace is pre-production: don't ask for migration or compatibility with data that doesn't exist. Review is not where
product decisions get made; a concern that needs a new state machine, authority, public contract, retry policy, or
capability is a decision for Scott. A concern outside the issue's goal is a suggested follow-up issue.

## Review passes

- **First review:** read the whole change once, including the callers, consumers, and tests it affects. Return a
  verdict (pass, pass with accepted risk, needs repair, needs Scott's decision, or too big: split it) and a finite list
  of findings. Stop when the change has been reviewed once.
- **Fix check:** after accepted findings are fixed, check only those findings, the repair diff, anything it directly
  affects, and the current-head CI. Don't reopen untouched code. If you happen to see a new blocker outside that scope,
  report it and stop.

## Finding format

```md
### [P1|P2|P3] [evidence|remove|defect] Short imperative title

**Location:** `path:line`
**Trigger and sequence:** ... (defects only)
**Impact:** ...
**Proof or fix:** ...
```

Be specific and brief. A clean review says there are no blocking findings; don't invent work to look thorough.
