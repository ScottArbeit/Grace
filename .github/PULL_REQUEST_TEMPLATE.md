# Pull Request

## Linked issue

Closes #

## Summary

## Touched paths

## Owned-path compliance

- [ ] My changed paths match the linked issue's owned paths.
- [ ] Any sensitive/shared path edits are explicitly allowed by the issue.
- [ ] I did not create or edit alternate task ledgers outside the issue/PR workflow.

## Validation profile and public-boundary evidence

- Validation profile:
- Public behavior or docs-only validation target:
- RED evidence or docs-only waiver:
- Focused validation:

## Minimum detail gate evidence

- Invariant tuple proved or docs-only waiver:
- Forbidden implementation shapes avoided:
- Positive / negative / regression / boundary tests or waivers:
- High-risk adversarial examples covered or waived:
- Selected risk-surface traps addressed or waived:

## Test coverage changes

Choose one:

- [ ] Tests added or updated; list the test files and covered behavior below.
- [ ] No tests added; explain why new tests were not required for this change.

Details:

## Reviewer pass

- [ ] Owned paths and forbidden paths reviewed against the issue.
- [ ] Sensitive surfaces reviewed and marked below.
- [ ] README, CONTRIBUTING, AGENTS, and docs drift checked.
- [ ] Skipped validation is explicitly listed with a reason.

## Risk surfaces

- [ ] Auth, authorization, tenant, or secrets
- [ ] Storage, Cosmos DB, Service Bus, Redis, or Aspire
- [ ] CLI public contract
- [ ] Server or API contract
- [ ] Orleans actor behavior
- [ ] SDK or client contract
- [ ] Deployment, Docker, or GitHub Actions
- [ ] Docs or workflow
- [ ] None of the above

## Docs impact

Choose one:

- [ ] Required; updated relevant docs.
- [ ] Docs impact: None - reason
- [ ] Not applicable; no user-facing, contributor-facing, or agent-facing behavior changed.

## Validation run

- [ ] `pwsh ./scripts/validate.ps1 -Fast`
- [ ] `pwsh ./scripts/validate.ps1 -Full`
- [ ] Focused tests: command
- [ ] Formatting/linting: command
- [ ] Manual validation: command or steps

## Validation not run

## Residual risks

## Review/fix prevention

For each Codex Code Review Bot or GitHub review finding fixed in this PR, include:

- Root-cause class:
- Current issue, sibling issues, template, or agent docs update needed:

Root-cause classes:

- acceptance-criteria / negative-proof gap
- contract-propagation gap
- CLI mode / side-effect interaction
- auth / materialization / traversal ordering gap
- algorithm adversarial-case gap
- validation / stale-evidence gap
- ordinary implementation mistake

## Rollback or recovery notes

## Docs follow-up required
