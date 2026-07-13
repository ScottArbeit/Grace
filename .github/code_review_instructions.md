
# Grace Code Review Instructions

This file guides automated and human code review for Grace pull requests. Repository-local `AGENTS.md`, nested guidance,
issue text, and PR body evidence remain authoritative for the specific change.

## Review priority

Review should favor correctness, contract alignment, and proof quality over style nits. Do not comment on formatting that
Fantomas, MarkdownLint, compiler warnings, or existing validation tooling will catch unless the relevant tool is not part
of the PR validation path.

## F# standards

### Use `task { }`; do not introduce `async { }`

Grace code should use the F# `task { }` computation expression for asynchronous code. New `async { }` code should be
considered an error unless a local file-level instruction explicitly requires it.

### Preserve correlation IDs for actor calls

Actor calls should preserve `CorrelationId` through `RequestContext.Set`. Prefer the ActorProxy extension helpers such as
`Branch.CreateActorProxy` and `Repository.CreateActorProxy`; inspect direct `IGrainFactory.GetGrain<'T>` usage carefully.

## Spec and issue alignment

For every PR, compare the diff with the linked issue or spec:

- required behavior implemented
- non-goals and forbidden implementation shapes respected
- owned paths respected or path expansion recorded
- public behavior under test actually proven
- residual risk stated honestly

Flag accepted inputs, flags, DTO fields, events, or parameters that are accepted but not implemented, not rejected, and
not explicitly classified as informational.

## Contract propagation

When any public or durable contract changes, verify all applicable surfaces are updated or waived:

- `Grace.Types` DTOs, commands, events, discriminated unions, serializers, and defaults
- `Grace.Shared` parameters and helpers
- server route parsing, validation, authorization, error shape, and route metadata
- CLI parser, JSON output, stdout/stderr, help, schema, and examples
- SDK or facade client
- static OpenAPI and generated clients
- events, webhooks, SignalR, watch, search, and projections
- tests, docs, ADRs, and agent guidance

Stale generated artifacts or stale client contracts are review findings even when runtime tests pass.

## Authority, lifecycle, and ordering

Give extra attention to Grace's repeated high-risk surfaces:

- current request body versus durable stored authority
- current configuration versus recorded placement/route evidence
- stale status/cache snapshots versus re-read state at mutation time
- terminal lifecycle states versus retained retry evidence
- idempotent replay after cleanup, partial success, or process restart
- authorization before materialization, SAS issuance, publication, search projection, or webhook delivery
- cleanup/rollback safety when a side effect succeeds but an actor or durable write rejects
- hidden or unauthorized resources as no-oracle behavior

If a PR makes a decision before a mutation/materialization/publication window, look for a revalidation point immediately
before the side effect.

## Testing and proof

Prefer findings that identify missing false-positive-resistant proof:

- positive, negative, regression, boundary, replay/retry, and stale-authority coverage
- tests that would fail if the old unsafe behavior returned
- authorization and non-observability tests for hidden, missing, or cross-scope resources
- deterministic runtime tests instead of arbitrary sleeps
- generated-contract and docs freshness checks when public surfaces change
- final validation evidence on the current head

## Repeated review cycles

If a PR reaches three substantive review cycles, or two cycles in the same invariant family, recommend stabilization
rather than another one-off patch. Ask for a review timeline, invariant family, stabilization ledger, focused proof, and
updates to the linked issue or future sibling issues.

Use these root-cause lanes in review comments or PR evidence:

- product-decision gap
- contract-propagation gap
- authority-source gap
- stale-snapshot / interleaving gap
- lifecycle / retry gap
- authorization / materialization ordering gap
- negative-proof gap
- validation freshness gap
- slice-boundary gap
- ordinary implementation mistake

## Tone

Be specific and actionable. Name the path, the invariant, why it matters, and the smallest proof or code change that
would close the finding. Avoid speculative rewrites when the issue can be fixed by clarifying the invariant or adding a
focused guard.
