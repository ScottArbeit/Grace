# Pull Request

## Linked issue and outcome

- Related issue:
- User-visible outcome:
- Why this matters for Grace:

For PRs targeting `main`, use `Closes #123` when merge should close the issue. For PRs targeting an epic integration
branch, use non-closing wording such as `Related to #123` or `Part of #597`.

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

## Scope and changed paths

- Changed paths:
- Owned-path compliance:
- Owner-approved path or scope expansion, or none:
- Accidental or unrelated changes checked:

## Contract and persistence propagation

List only surfaces this PR changes or must prove unchanged.

| Surface | Updated, unchanged, deferred, or N/A | Proof or reason |
| --- | --- | --- |
| Public DTO, route, CLI, SDK, event, or error contract | | |
| Persisted state or filesystem layout | | |
| OpenAPI or generated artifacts | | |
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

## Current-head CI

- Final head SHA:
- GitHub `Validate` state:
- GitHub Actions run link:
- Result applies to final head: yes/no

## R1 discovery review

- Reviewer configuration:
- Candidate head SHA:
- `dev-process/CODE_REVIEW.md` mode: Discovery review
- Verdict: PASS | PASS WITH ACCEPTED RISK | REPAIR | OWNER DECISION | SUPERSEDE OR SPLIT
- Review Discovery Ledger link or summary:
- Rejected, deferred, accepted-risk, or owner-decision dispositions:

If R1 passed without accepted repairs and final-head CI is green, R2 is not required.

## Repair pass

Complete only when R1 produced accepted findings.

- Issue-owner worker:
- Consolidated repair commits:
- Ledger status map:
- Focused regression proof:
- Scope and non-goals preserved:

## R2 closure review

Complete only when accepted repairs were made.

- Reviewer configuration:
- Final head SHA:
- `dev-process/CODE_REVIEW.md` mode: Closure review
- Verdict: VERIFIED | NOT VERIFIED | OWNER DECISION | SUPERSEDE OR SPLIT
- Ledger items verified:
- Direct repair regressions:
- Final-head proof and CI verified:

There is no automatic R3. A new material invariant, authority boundary, state machine, product semantic, or scope
expansion stops the run for owner disposition.

## Residual risk, skipped proof, and recovery

- Residual risk:
- Skipped proof:
- Rollback, repair, or cleanup notes:
- Follow-up issues that are real and explicitly out of scope:

## Merge readiness

- [ ] The linked issue still describes the implemented outcome and supported world.
- [ ] Explicit non-goals remain absent and no half-active capability was introduced.
- [ ] Focused proof and final-head GitHub `Validate` pass.
- [ ] R1 discovery review is complete.
- [ ] Accepted R1 ledger items, if any, are closed by R2.
- [ ] Public, persisted, generated, and documentation surfaces are current or explicitly waived.
- [ ] Residual risk and skipped proof are visible.
- [ ] No owner stop condition remains unresolved.
