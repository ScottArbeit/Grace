# Grace Code Review Instructions

This file guides automated and human review for Grace pull requests. The linked issue, current owner decisions,
`dev-process/QUALITY-CONTRACTS.md`, `dev-process/CODE_REVIEW.md`, and repository-local `AGENTS.md` files remain
authoritative.

Every independent review must declare one mode:

- **R1 discovery review:** one broad supported-world review that produces a finite ledger.
- **R2 closure review:** one targeted verification of accepted repairs, direct repair regressions, and final-head proof.

Do not perform an R1-style whole-diff search during R2.

## Review priority

Favor supported-path correctness, contract alignment, authority and effect ordering, and proof truth over style nits. Do
not comment on formatting that Fantomas, MarkdownLint, compiler warnings, or required validation already enforce.

Review is not product authority. A concern requiring a new product semantic, state machine, authority, public lifecycle,
retry policy, or capability is an owner-decision result, not an ordinary finding.

## Product V1 realistic-producer gate

For each Product V1 finding, name:

1. the supported client, workflow, dependency, platform mechanism, ordinary retry, restart, redelivery, cancellation, or
   operator action that produces it;
2. the shortest concrete supported sequence;
3. why the trigger is plausible in normal supported use;
4. the violated contract or included-capability obligation;
5. the material impact.

Technical reachability alone is insufficient. Suppress scenarios requiring deliberate protocol misuse, manual state
corruption, an unsupported caller or adversary, contradictory identity reuse, or a chain of independent speculative
failures.

An uncommon scenario remains required only when one plausible supported trigger can cause authorization or isolation
failure, unrecoverable valuable-data loss or corruption, or an irreversible external effect. State that exception.

## F# standards

### Use `task { }`; do not introduce `async { }`

Grace asynchronous code uses the F# `task { }` computation expression. Treat new `async { }` as an error unless a
local file-level instruction explicitly requires it.

### Preserve correlation IDs for actor calls

Actor calls should preserve `CorrelationId` through `RequestContext.Set`. Prefer ActorProxy extension helpers such as
`Branch.CreateActorProxy` and `Repository.CreateActorProxy`; inspect direct `IGrainFactory.GetGrain<'T>` use carefully.

### Preserve project conventions

Apply the nearest `AGENTS.md` rules for module ownership, public XML comments, F# formatting, resumable computation
expressions, and focused test placement. Do not report a convention already enforced by required tooling unless that
tooling is absent from the PR proof.

## Issue and capability alignment

Compare the diff with the linked issue and Outcome Charter:

- one user-visible outcome is delivered;
- one primary invariant family remains in scope;
- supported actors, environment, topology, and producer paths are honored;
- explicit non-goals and deferred capabilities remain absent;
- the algorithm-witness decisions are translated rather than contradicted;
- owned paths are respected or owner-approved expansion is recorded;
- public behavior under test is actually proven;
- residual risk is stated honestly.

Flag accepted inputs, flags, DTO fields, events, routes, settings, or timers that are accepted but not implemented,
rejected, or explicitly informational.

Stop rather than expand the finding set when the PR needs another durable partial-state lifecycle, authority boundary,
product semantic, or third pre-tracer enabling PR.

## Contract propagation

When a public or durable contract changes, verify applicable surfaces are updated or explicitly unchanged:

- `Grace.Types` DTOs, commands, events, discriminated unions, serializers, and defaults;
- `Grace.Shared` parameters and helpers;
- server route parsing, validation, authorization, error shape, and route metadata;
- CLI parser, JSON output, stdout and stderr, help, schema, and examples;
- SDK or facade client;
- static OpenAPI and generated clients;
- events, webhooks, SignalR, watch, search, and projections;
- persisted state and filesystem layout;
- tests, docs, ADRs, and agent guidance.

Do not require an exhaustive N/A inventory. Review surfaces the slice actually changes or promises to keep coherent.
Stale generated artifacts are findings when the source contract changed.

## Authority, lifecycle, and ordering

Apply these checks only when selected capabilities create them:

- current request versus durable authority;
- current configuration versus recorded placement or route evidence;
- stale snapshots versus re-read state at a consequential mutation;
- effect order and commit point established by the algorithm witness;
- partial physical or durable residue after supported failure;
- terminal states versus retained retry evidence;
- supported replay after cleanup, partial success, or restart;
- authorization before materialization, SAS issuance, publication, search projection, or webhook delivery;
- cleanup safety when a physical effect succeeds but a durable write rejects;
- hidden or unauthorized resources as no-oracle behavior.

Do not invent generalized recovery, automatic reconciliation, hostile-local-process defense, or broad platform parity when
the Product V1 issue explicitly excludes them.

## Testing and proof

Look for false-positive-resistant proof:

- positive, negative, regression, and boundary cases relevant to the supported world;
- deterministic failure injection at included effect boundaries;
- tests that fail if the previous unsafe behavior returns;
- authorization and non-observability proof for included hidden or cross-scope behavior;
- deterministic completion signals rather than arbitrary sleeps;
- generated-contract and documentation freshness when public surfaces change;
- final validation evidence on the current head.

Reject hand-built states that no supported producer can create.

## R1 discovery review

R1 may inspect the complete supported diff and relevant callers, consumers, contracts, and proof. It must end with a
finite verdict and ledger. Each actionable finding includes supported producer, reproduction, contract basis, likelihood
basis, impact, required invariant, and closure proof.

Do not continue searching after the supported world has been reviewed once. Repair commits close the ledger; they do not
reopen discovery.

## R2 closure review

R2 checks only:

- accepted R1 ledger items;
- repair hunks and direct callers or consumers;
- direct regressions introduced by repair;
- final-head focused proof, generated checks, and GitHub `Validate`;
- scope and non-goal preservation.

A straightforward incomplete repair may be corrected and rechecked in the same closure context. If R2 incidentally
exposes a supported-world merge blocker outside the frozen ledger and direct repair-regression scope, return one
`DISCOVERY ESCAPE` item and stop. Do not ignore it or continue searching. A new material invariant, authority, state
machine, public semantic, or capability requirement is an owner stop. Neither case starts an automatic R3 whole-diff
review.

## Tone

Be specific and actionable. Name the path, supported producer, invariant, impact, and smallest proof or correction that
closes the concern. A clean review reports no blocking findings. Do not manufacture work to appear thorough.
