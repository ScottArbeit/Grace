---
name: code-review-stabilizer
description: >-
  Detects repeated automated code-review/fix loops and forces a deeper invariant-ledger stabilization pass before
  more review requests. Use during Grace issue/PR orchestration when Codex or another reviewer repeatedly finds
  substantive adjacent defects, especially in storage, actors, persistence, retries, concurrency, authorization,
  public contracts, or other high-risk code.
argument-hint: "Issue/PR number, reviewer name, or review-cycle concern"
---

# Code Review Stabilizer

Use this skill when a Grace pull request is stuck in repeated code-review back-and-forth, or when the orchestrator sees
review findings that suggest the implementation model is under-specified.

This skill exists to stop expensive one-off patch loops. When review findings keep moving to adjacent edge cases, pause
normal fix-and-review iteration, name the missing invariants, prove them, and only then request another review.

## Trigger thresholds

Count a **substantive review cycle** as:

1. A reviewer, usually Codex Code Review, reports one or more behavior, correctness, security, durability, concurrency,
   contract, or maintainability findings.
1. A worker pushes a fix commit or fix series.
1. The next review reports another substantive finding on the new head.

Do not count purely administrative comments, duplicate comments, formatting-only nits, CI flakes, stale review threads
from previous review passes whether resolved or unresolved, maintainer-accepted future-leaf deferrals, or reviewer
comments that the orchestrator explicitly classifies as invalid.

### Default threshold

- **Cycle 1:** normal review/fix behavior is acceptable.
- **Cycle 2:** continue, but watch for repeated themes and add a short prevention note to the PR.
- **Cycle 3:** pause normal patching and start a stabilization pass.
- **Cycle 4:** hard stop. Do not request another review until a stabilization ledger and self-review are posted.

### High-risk threshold

Start the stabilization pass after **2 substantive cycles** when the touched surface includes any of these:

- Watch state, IPC/status contracts, branch-switch safety, local working-tree mutation, or runtime timers
- storage routing, CAS, object placement, blob reads/writes, cleanup, retention, or compaction
- Orleans actors, idempotency, replay, retries, reminders, timers, or durable actor state
- metadata accounting, reference counts, dedupe indexes, or garbage-collection eligibility
- auth, authorization, ownership, tenant/repository scope, secrets, or token authority
- public HTTP DTOs, OpenAPI, SDKs, CLI contracts, serialized events, or persisted shapes
- concurrency, TOCTOU windows, async side-effect ordering, partial failure, or recovery
- migrations, data reset assumptions, destructive operations, or irreversible state transitions

A fourth substantive cycle is always a hard stop, even outside high-risk areas.

### Review-session threshold

Count each completed Codex Code Review Bot pass on a distinct PR head as a **review session**, including no-issues
outcomes, top-level findings, inline review-thread findings, or mixed stale and fresh comments. A manual missed-ack
trigger that causes the bot to review the same head also counts as a session. Do not count repeated status checks, CI
reruns, or unresolved stale threads without a new completed bot pass.

If a Grace PR has more than three Codex Code Review Bot review sessions, pause before assigning another routine fix
worker even when fewer than three sessions count as substantive cycles. Build the review timeline, separate stale,
duplicate, invalid, deferred, and no-issue sessions from fresh findings, then decide whether the issue is missing
invariants, needs a named future-leaf deferral, or requires a structural stabilization ledger before the next review
request.

## Immediate stop signals

Invoke this skill immediately, regardless of cycle count, when any of these appear:

- Multiple findings share the same theme, such as replay, stale evidence, counting semantics, authorization, or cleanup.
- A fix for one finding clearly creates or exposes the next finding.
- Review comments say things like "fresh evidence in this commit", "current head", "after the previous fix", or
  equivalent language.
- Findings concern authority, ownership, durable state, side-effect ordering, idempotency, retries, or contract drift.
- Tests are growing but confidence is not improving.
- One command, DTO, or actor message is carrying several semantic roles that need to be separated or named.

## Stabilization workflow

### 1. Build the review timeline

Collect a concise table from the PR timeline:

- review number or timestamp
- reviewed commit SHA
- finding title and affected path
- fix commit SHA
- recurring theme
- classification: last-fix regression, adjacent invariant gap, latent same-surface issue, invalid/waived, or unrelated

If the reviewer is finding unrelated issues outside the PR scope, say so plainly. Most of the time, the problem is not
reviewer drift; it is an under-specified invariant family in the current PR surface.

### 2. Identify the invariant family

Name the repeated model, not just the local bug. Examples:

- durable replay authority
- fresh request versus idempotent replay semantics
- metadata reference-count contribution semantics
- cleanup-aware recovery evidence
- current-state versus request-body authority
- authorization before materialization
- all-or-nothing side-effect ordering
- compatible-current concurrency
- public contract propagation

Prefer Grace domain language from the issue, PR, code, and existing docs. Do not invent new durable nouns unless the
current language is insufficient.

### 3. Write a stabilization ledger

Post a compact ledger to the linked issue and the PR. The ledger must become the acceptance target before another review
request.

The ledger should include:

- scope and owned surfaces
- the invariant statements to satisfy
- proof obligations for each invariant family
- explicit out-of-scope boundaries
- validation expectations
- a required final self-review format

Use clear, behavior-level language. Avoid implementation micro-instructions unless the invariant cannot be expressed
without naming a concrete seam.

### 4. Freeze one-off review requests

After the ledger is posted, do not ask for another normal review until the current worker has:

1. inspected the current PR head
1. resolved or classified every unresolved finding
1. mapped the implementation to the ledger
1. added or updated focused proof
1. posted the required status-map self-review
1. run appropriate validation

### Future leaf deferrals

If the PR targets an epic integration branch, classify each fresh latest-head finding against the current leaf's scope
before assigning a fix worker. A valid finding may be resolved as future work only when all of these are true:

- the finding is about behavior explicitly out of scope for the current leaf;
- the future leaf issue already exists or is created before resolution;
- the future issue body is updated with the exact finding, invariant, and proof obligation;
- the PR reply names the future issue and explains why the current PR must not implement it;
- the PR `Review Status` records the deferred disposition.

Do not defer prerequisites that make the current leaf's contract trustworthy. If later leaves consume a fact, authority
signal, persisted field, status flag, or trust predicate produced by the current leaf, then the current leaf owns making
that surface reliable.

### 5. Prove before patching broadly

Use the `tdd` skill when changing code. Prefer behavior-level tests over source-string tests. Source-string tests are a
last resort for generated/source-shape proof when no stable behavioral seam exists.

Use this status-map shape in the worker handoff or PR evidence:

| Invariant | Status | Code seam | Test/proof seam | Residual risk |
| --------- | ------ | --------- | --------------- | ------------- |
| `<ledger invariant>` | `<allowed status>` | `<file/function>` | `<test or validation>` | `<risk or none>` |

For every ledger item, the final self-review must use exactly one status:

- `implemented and proven`
- `implemented but proof incomplete`
- `waived`, with reason
- `out of scope`, with reason
- `not applicable`, with reason

A PR is not stabilized if the status map is missing or if broad phrases like "handled", "wired up", "covered", or
"reviewed" appear without naming the implementation and proof seam.

## Ledger template

Use this template as a starting point. Keep it compact enough to survive as an issue/PR comment.

```markdown
## Review stabilization ledger

This PR has crossed the repeated-review threshold. Treat the remaining work as a stabilization pass, not as another
one-off review fix.

### Scope

Applies to: [owned behavior surfaces].

Out of scope: [explicit non-owned surfaces].

### Acceptance invariants

1. **[Invariant name].** [Behavior-level invariant.]
1. **[Invariant name].** [Behavior-level invariant.]
1. **[Invariant name].** [Behavior-level invariant.]

### Required proof

Focused proof must cover:

- [positive/concurrency/replay/recovery/negative case]
- [boundary case]
- [contract or docs proof/waiver]

### Required self-review before another review request

Before requesting another review, post a status map for each invariant using one of:
`implemented and proven`, `implemented but proof incomplete`, `waived`, `out of scope`, or `not applicable`.

Do not request another review until the status map, focused tests, validation, and residual-risk notes are present.
```

## Grace-specific stabilization ledger examples

Use these examples as patterns, not fixed text.

### Replay and retry

- Replay authority is durable-first.
- Fresh finalization and same-operation replay have separate rules.
- Retry-body evidence may be used only for validation against durable state, not as replay side-effect authority.
- Replay success includes idempotent repair for side effects that may have partially failed after durable state was
  written.

### Metadata accounting

- Reference counts increase only for explicit finalize contributions or explicit reclaimable-range reactivation.
- Generic repair/append merges preserve already-active ranges.
- Operation identity preserves both same-operation idempotency and distinct-session contributions.
- Merge-time checks reject stale evidence without rejecting benign compatible-current concurrency.

### Materialization and ownership

- Ownership/authorization is proven before materializing or reading authoritative bytes.
- Claimed reuse hydrates from authoritative placement, not from caller-provided payload bytes.
- Cleanup changes which replay evidence is valid.
- A multi-range block uses cover semantics rather than a single whole-block range assumption.

### Public contracts

- Any changed public DTO, OpenAPI shape, SDK behavior, CLI output, event payload, or persisted shape is updated and
  tested, or an explicit no-contract-change waiver is recorded.

## Review request after stabilization

When the stabilization pass is complete, resume the normal Grace PR review loop. Do not bypass the manual trigger lock:
wait for Codex Code Review Bot on the pushed head, and use the documented missed-ack guard only when the bot has not
acknowledged the current head. If a manual missed-ack trigger is allowed, use language like:

```markdown
@codex review this PR against the Review stabilization ledger posted above. Please focus on whether every ledger
invariant is implemented and proven, whether any status-map item is overstated, and whether any adjacent edge case in
the same invariant family remains untested. Do not limit the review to the latest patch.
```

## Orchestrator responsibilities

The orchestrator must:

- watch the review/fix cycle count
- invoke this skill at the threshold
- prevent another routine review request after the hard stop
- ensure the issue and PR contain the ledger
- ensure the worker posts the status map
- ensure any future-leaf deferral names and updates the future issue before resolving the finding
- require focused validation before review resumes
- update PR status with residual risks and skipped validation

If stabilization shows the PR is too broad, split follow-up work into new issues rather than silently expanding the PR.
Keep GitHub issues and PRs as the durable coordination surfaces; do not create alternate task ledgers unless the user
explicitly asks.

## Output expectations

When this skill is invoked, produce one or more of:

- a review-cycle analysis table
- a stabilization ledger suitable for posting to the issue and PR
- a worker resume prompt
- a Codex review request prompt
- a self-review checklist
- proposed issue/PR body updates

Be candid. If the process failure is issue underspecification, say that. If the reviewer is finding unrelated work, say
that and recommend scope control.
