# Pull Request

## Linked issue, outcome, and delivery mode

- Related issue:
- Parent epic, if any:
- User-visible or value-bearing outcome:
- Why this matters for Grace:
- Delivery mode: mainline slice | epic integration branch | final epic release

For PRs targeting `main`, use `Closes #123` when merge should close the issue. For PRs targeting an epic integration
branch, use non-closing wording such as `Related to #123` or `Part of #597`.

## Baseline admissibility

- Base branch and exact SHA:
- Eventual delivery target:
- Restore, build, and integration baseline proof:
- Semantic merge or rebase conflicts: none | owner decision link
- Prior lineage used as salvage, or explicit ancestry requirement:
- Baseline Admissibility verdict:

## Supported world and quality contract

- Baseline and overrides:
- Supported actor or client:
- Environment and topology:
- Supported producer paths:
- Explicit non-goals and deferred capabilities:

## Primary invariant and algorithm readiness

- Primary invariant:
- Authoritative source and commit point:
- Algorithm witness and verdict, or justified N/A:
- Required effect-order, residue, restart, retry, cleanup, or concurrency rules:

## Scope and delivery delta

- Changed paths in this PR:
- Owned-path compliance:
- Owner-approved expansion, or none:
- Three-dot issue delta checked:
- Eventual delivery delta against `main` checked:
- Deferred or half-active capability inherited from the base: none | owner disposition
- Accidental or unrelated changes checked:

## Contract and persistence propagation

List only surfaces this PR changes or must prove unchanged.

| Surface | Updated, unchanged, deferred, or N/A | Proof or reason |
| --- | --- | --- |
| Public DTO, route, CLI, SDK, event, or error contract | | |
| Persisted state or filesystem layout | | |
| OpenAPI or generated artifacts | | |
| Runtime or Aspire topology | | |
| Docs and examples | | |
| Tests and validation | | |

## Proof

- Focused failing proof or prior regression evidence:
- Positive, negative, and boundary proof:
- Failure-injection, restart, replay, or concurrency proof when included:
- Formatting, linting, generated-artifact, freshness, or syntax checks:
- Manual or runtime evidence:
- Optional local Fast or Full, with reason:
- Validation not run and reason:

## Factory run

- Execution mode: direct single-agent | controller/worker
- Root issue session:
- Implementation owner: root | worker thread
- Child context policy: `fork_turns = "none"` | recorded read-only scout exception
- Diagnostic scout used: no | yes, question and result
- Replacement worker used: N/A in direct mode | no | yes, reason
- Owner stop encountered: no | link and disposition

## Current-head CI

- Final head SHA:
- GitHub `Validate` state:
- GitHub Actions run link:
- Result applies to final head: yes/no

## R1 discovery review

- Reviewer configuration: read-only, no inherited implementation turns
- Candidate head SHA:
- `dev-process/CODE_REVIEW.md` mode: Discovery review
- Verdict: PASS | PASS WITH ACCEPTED RISK | REPAIR | OWNER DECISION | SUPERSEDE OR SPLIT
- Review Discovery Ledger link or summary:
- Rejected, deferred, accepted-risk, or owner-decision dispositions:

If R1 passed without accepted repairs and final-head CI is green, R2 is not required.

## Repair pass

Complete only when R1 produced accepted findings.

- Same implementation owner continued or resumed:
- Consolidated repair commits:
- Ledger status map:
- Focused regression proof:
- Scope, delivery mode, delivery delta, and non-goals preserved:

## R2 closure review

Complete only when accepted repairs were made.

- Reviewer: resumed R1 | fresh read-only reviewer because R1 unavailable
- Final head SHA:
- `dev-process/CODE_REVIEW.md` mode: Closure review
- Verdict: VERIFIED | NOT VERIFIED | DISCOVERY ESCAPE | OWNER DECISION | SUPERSEDE OR SPLIT
- Ledger items verified:
- Direct repair regressions:
- Final-head proof and CI verified:

There is no automatic R3. A new material invariant, authority boundary, state machine, product semantic, scope expansion,
or delivery-base defect stops the run for owner disposition.

## Residual risk, skipped proof, and recovery

- Residual risk:
- Skipped proof:
- Rollback, repair, or cleanup notes:
- Follow-up issues that are real and explicitly out of scope:

## Merge readiness

- [ ] The linked issue still describes the implemented outcome and supported world.
- [ ] Baseline Admissibility is current and no semantic conflict remains unresolved.
- [ ] The eventual delivery delta against `main` fits the capability budget.
- [ ] Explicit non-goals remain absent and no half-active capability was introduced.
- [ ] Focused proof and final-head GitHub `Validate` pass.
- [ ] R1 discovery review is complete.
- [ ] Accepted R1 ledger items, if any, are closed by R2.
- [ ] Public, persisted, generated, runtime, and documentation surfaces are current or explicitly waived.
- [ ] Residual risk and skipped proof are visible.
- [ ] No owner stop condition remains unresolved.
