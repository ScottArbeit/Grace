# Grace Code Review Instructions

This is the review rubric for Grace pull requests, for human and automated reviewers. The linked issue and Scott's
recorded decisions say what the change is supposed to do; this file says how to judge it.

## What a finding is

Review for correctness of the behavior the issue asks for. A finding needs all of:

1. **A realistic trigger:** a supported client, workflow, dependency behavior, ordinary retry, restart, redelivery,
   cancellation, or normal operator action that produces the problem. Technical reachability is not enough.
2. **A concrete sequence:** the shortest path from that trigger to the failure.
3. **A real impact:** wrong state, wrong result, a security or isolation failure, lost data, or a test that proves
   nothing.
4. **A way to close it:** the test or check that would show the fix works.

Skip scenarios that need deliberate protocol misuse, hand-corrupted state, an unsupported caller, or a chain of
unlikely independent failures. One exception: an uncommon but plausible trigger still counts when it would cause an
authorization or isolation failure, unrecoverable data loss, or an irreversible external effect. Say so when you use it.

Grace is pre-production. Do not ask for migration or compatibility with data that does not exist.

Review is not where product decisions get made. If a concern needs a new state machine, authority, public contract,
retry policy, or capability, report it as a decision for Scott rather than a defect.

## What to look at

- **Scope:** the diff delivers the issue's outcome, leaves its stated non-goals out, and adds no half-wired flags,
  fields, routes, timers, or settings. An accepted input is implemented, rejected, or documented as informational.
- **Types and data shape:** can the new types represent states that should be impossible? Are two facts stored when
  one could be derived? Do values that must change together travel together, or are they matched up by position or
  convention? Are booleans, nulls, or sentinels standing in for real cases? Does the wire and persisted shape match the
  in-memory change? Extra type machinery should buy a real invariant.
- **Authority and ordering:** after any wait, is state re-read before a consequential write, materialization, SAS
  issuance, or publication? Is authorization checked before content is materialized or a resource is revealed? Are
  retries, replays, and partial failures safe for the capabilities the change includes?
- **Contract propagation:** when a public or durable contract changes, the surfaces listed in `src/AGENTS.md` that it
  reaches are updated, including static OpenAPI and generated clients.
- **Proof:** tests fail if the bug returns; they observe the real outcome, not an intermediate state; they use
  deterministic signals rather than sleeps; they build only states a supported producer can create; and CI and
  test claims refer to the current head.

## F# standards

- Use `task { }`. New `async { }` is an error unless a file says otherwise.
- Avoid `let rec` and `for ... in` loops inside `task { }` and `backgroundTask { }` (FS3511). FS3511 warnings are
  regressions.
- Actor calls preserve `CorrelationId` through `RequestContext.Set`; prefer the `CreateActorProxy` helpers over raw
  `IGrainFactory.GetGrain`.
- Don't comment on formatting that Fantomas, markdownlint, or compiler warnings already enforce.

## Review passes

- **First review:** read the whole change once, including the callers, consumers, and tests it affects. Return a
  verdict (pass, pass with accepted risk, needs repair, needs Scott's decision, or too big: split it) and a finite list of
  findings. Stop when the change has been reviewed once.
- **Fix check:** after accepted findings are fixed, check only those findings, the repair diff, anything it directly
  affects, and the current-head CI. Don't reopen untouched code. If you happen to see a new blocker outside that scope,
  report it and stop rather than starting another full review.

## Finding format

```md
### [P1|P2|P3] Short imperative title

**Location:** `path:line`
**Trigger and sequence:** ...
**Impact:** ...
**Fix and proof:** ...
```

Be specific and brief. A clean review says there are no blocking findings; don't invent work to look thorough.
