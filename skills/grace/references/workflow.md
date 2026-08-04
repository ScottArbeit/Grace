
# Grace Workflow

Load this reference for non-trivial planning, issue orchestration, implementation, review, PR, or cleanup work.

## Current Source Of Truth

- Root instructions: `AGENTS.md`
- Code instructions: `src/AGENTS.md`
- Process details: `docs/Development process.md`
- Contributor docs: `CONTRIBUTING.md`

Re-read these files during the current session before relying on older memory or this summary.

## Planning Versus Tracked Work

- When the user says `Plan <work item>`, plan in chat and stop. Do not create GitHub issues unless asked.
- When asked to create issues, use the Grace agent task shape and stop before implementation unless asked to implement.
- For tracked multi-step work, create an epic parent issue, one linked sub-issue per implementation step, native GitHub
  parent relationships, a DAG in the parent issue, and an epic checklist that is updated as sub-issues complete. Use the
  concrete `addSubIssue` GraphQL workflow in `docs/Development process.md` when creating the native parent/child
  relationships.
- For non-trivial epics, identify an early tracer-bullet vertical slice before broad parallelization. The slice should
  prove one narrow user-visible behavior through the closest stable public boundary, crossing the main contract,
  runtime, persistence, validation, and documentation surfaces that later slices are likely to reuse. Use the result to
  refine child issues, owned paths, validation profiles, and parallelization boundaries.
- When creating an epic plus child issues from PowerShell, write each issue body to a separate temporary Markdown file
  with `Set-Content` or equivalent small commands, then call `gh issue create --body-file` for each issue. Do not build
  one huge inline script containing all issue bodies. After the child issue numbers exist, patch the epic body with the
  real numbers, then use GraphQL `addSubIssue` for the native parent relationships.
- When implementing an epic, always use an explicit `epic-integration-branch`. Create
  `epic/<parent-issue>-<short-slug>` from `origin/main`, route sub-issue pull requests to that branch, and use the
  final epic-to-`main` pull request as the production release candidate. Do not use direct-to-`main` epic slices.
- Before assigning a sub-issue, require the minimum detail gate:
  - invariant tuple
  - forbidden implementation shapes
  - positive, negative, regression, and boundary tests
  - high-risk adversarial examples
  - selected risk-surface traps
  - explicit N/A waivers with reasons
- Write the minimum detail gate as review-prevention guidance. Predict likely current-head review findings, such as
  weak tests, stale identity assumptions, contract drift, auth or materialization ordering mistakes, async gaps,
  stale generated artifacts, or validation gaps, using the existing issue fields instead of adding more ceremony.

## Issue-Owned Implementation

For non-trivial tracked work:

1. Confirm or create the GitHub issue.
1. Declare objective, context, owned paths, forbidden paths, risk surfaces, validation, docs impact, and definition of
   done.
1. Claim the issue with a comment, assign it to the authenticated GitHub user, and create an issue-owned
   branch/worktree from the selected base.
1. Inspect `git status --short --branch` before editing.
1. Work in vertical slices through the closest stable public boundary.
1. Run formatting or freshness checks first, then exactly one final build/test gate.
1. Commit after each completed slice.
1. Open a normal ready-for-review PR. Do not open draft PRs unless the user asks for a draft.

When a PR targets the default branch and should close an issue, reference the issue with one of GitHub's supported
closing keywords: `close`, `closes`, `closed`, `fix`, `fixes`, `fixed`, `resolve`, `resolves`, or `resolved`. For
epic-branch PRs, use non-closing wording such as `Related to #123` or `Part of #249`, then close the sub-issue manually
after merge.

For parallel work, separate product/DAG independence from merge/write-set independence. Parallelize only when the
expected write sets are disjoint enough to avoid predictable churn. Serialize or merge-queue branches that touch shared
project files such as `*.fsproj`, `Startup.Server.fs`, or the same test/helper files. For broad waves, consider a
preparatory compile-item or file-scaffold slice before later branches edit separate files.

For standalone non-epic issues, create issue branches from `origin/main` and target pull requests to `main`. For epics,
create `epic/<parent-issue>-<short-slug>` from `origin/main`, create sub-issue branches from the current
`origin/epic/...`, target sub-issue PRs to the epic branch, and use the final epic-to-`main` PR as the production
release candidate. Keep the epic branch refreshed from `origin/main`; ensure CI validates PRs targeting `epic/**`, or
record the CI gap and required local validation before assigning workers.

## Orchestration And Review

When acting as the main implementation orchestrator, follow the repo policy:

- Delegate coding and fixing tasks to worker subagents when the available tools and user authorization allow
  delegation.
- When spawning a subagent, always set `fork_turns` to `"none"` unless the assigned task specifically requires parent
  conversation history. Pass all required context explicitly in the subagent's task message.
- Do not replace the worker by locally implementing or validating code fixes from the orchestrator role.
- Include a status-reporting protocol in worker prompts. Ask the worker to maintain a temp status file outside the repo,
  for example `$env:TEMP\grace-agent-status\<issue-or-pr>-<task>.md`, with `phase`, `lastUpdate`, `changedFiles`,
  `validation`, `blockers`, and `nextStep`. Require updates before edits, before and after long validation or generation
  commands, before commit/push/handoff steps, and before the final response. Also ask for a short chat heartbeat
  roughly every five minutes while work continues.
- Require a lightweight implementation preflight before coding: acceptance criteria to prove, contract surfaces to
  update or waive, existing tests to fail or extend, adversarial cases, global options or modes, validation, touched
  paths, and any issue-owned path expansion needed before editing.
- Require a review-prevention self-review before worker handoff. The worker should inspect the actual diff against the
  declared quality contract, fix likely findings before push or handoff, and report first-pass review readiness plus
  residual risks.
- Ask worker subagents to finish with a handoff as soon as their assigned implementation or fix is validated and pushed.
  By default, the orchestrator owns GitHub issue updates, PR body updates, review-comment replies, conversation
  resolution, labels, checklists, merge state, and cleanup records. The orchestrator can start the next independent
  worker from a sufficient handoff before finishing wrap-up for the previous worker when dependencies and write sets
  make that safe.
- For every current PR head, start one fresh review subagent with model `gpt-5.6-terra`, reasoning effort `high`, and
  `fork_turns: none`. Give it complete issue, PR, worktree, branch, base, head, validation, and quality-contract context,
  and require it to read and follow the installed `dev-process/CODE_REVIEW.md`.
- Run the review subagent concurrently with required GitHub checks. Wait for both. If the head changes, both results
  are stale and both gates must run again.
- Keep the review subagent read-only. The orchestrator records its structured verdict and dispositions in the PR's
  `Review Status`, routes valid fix-now findings to a fresh implementation/fix worker, and starts a new reviewer plus
  checks after the fix is pushed.
- Grace does not use pull-request reactions, automatic review comments, or manual review triggers as review state.
- Aspire starts are uncapped and do not count against the implementation-worker budget. Record purposeful runs and
  outcomes, and avoid overlapping instances unless the accepted scenario requires concurrency.

Repeated review cycles need structural handling, not endless one-off fixes:

- A substantive cycle is a fresh latest-head behavior, correctness, concurrency, recovery, durability, authority,
  contract, or maintainability finding, followed by a worker fix, followed by another substantive latest-head finding.
- Do not count stale threads, duplicates, formatting-only comments, administrative comments, CI flakes, invalid
  findings, or maintainer-accepted deferrals.
- After one substantive cycle, continue the normal fix loop. After two, add a repeated-theme prevention note to
  `Review Status`. After three, pause one-off fixes and post a stabilization ledger to the issue and PR. After four,
  hard stop until the ledger is implemented, proven, and self-reviewed.
- Start stabilization after two substantive cycles for high-risk surfaces such as Watch state, IPC/status contracts,
  branch-switch safety, storage, actors, authorization, public contracts, persisted shapes, concurrency, recovery, or
  side-effect ordering.
- Count each completed fresh review-subagent pass on a distinct PR head as a review session, including no-issues and
  finding-producing verdicts. Do not count CI reruns, repeated status checks, or a replacement that never returned a
  verdict.
- If a PR has more than three completed review-subagent sessions even without three counted substantive cycles,
  audit the timeline before assigning another routine fix worker. Decide whether the issue is missing invariants,
  needs a future leaf deferral, or requires a structural ledger before the next review request.
- In an epic-branch PR, a valid finding may be resolved as future work only when it is explicitly outside the current
  leaf, the future issue already exists or is created first, the future issue body receives the exact finding and proof
  obligation, the PR reply names the future issue, and `Review Status` records the deferral.
- Do not defer prerequisites that make the current leaf trustworthy. If later leaves consume a fact, authority signal,
  persisted field, status flag, or trust predicate produced by the current leaf, the current leaf owns that reliability.

If subagent tools are unavailable or cannot be used under the active tool policy, state that limitation and preserve the
rest of the Grace workflow as far as possible.

## Validation Profiles

Use the profile from `docs/Development process.md`:

| Profile | Use For |
| ------- | ------- |
| `docs-only` | Markdown, HTML, static guidance, workflow docs |
| `domain-contract` | Types, DTOs, validators, serializers, hashes, shared helpers |
| `cli-command` | Grace CLI command behavior |
| `server-api` | HTTP handlers, server services, auth, persistence boundaries, API contracts |
| `actor-workflow` | Orleans actor behavior and durable state transitions |
| `sdk-client` | SDK surface or client contract changes |
| `deployment-runtime` | Aspire, emulators, Docker, Azure resources, scripts, runtime config |

Focused proof comes first. The broad commands below are optional local escalation tools; GitHub `Validate` is the
authoritative broad gate for the current pull-request revision.

Commands:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
npx --yes markdownlint-cli2 "**/*.md"
git diff --check
```

Use Fast for a concrete optional broad preflight, such as unavailable CI or unusually broad compile fan-out. Use Full
for local integration reproduction or diagnosis, not merely because an integration-related path was touched. Avoid
routine local broad validation immediately followed by required CI. Focused project build/test, formatting, freshness
checks, and `git diff --check` are required before commit; build the focused project before `--no-build` tests.

Before the Grace completion review gate, update the branch against its required base: current `origin/main` for
standalone non-epic issue branches, current `origin/epic/...` for sub-issue branches targeting an epic integration
branch, and current `origin/main` for the final epic-to-`main` PR. Verify ahead/behind, verify the scoped diff and that
no unexpected deletions are present, and rerun focused proof when relevant changes affect the slice. Push the refreshed
head, then require current GitHub `Validate` and a fresh review-subagent verdict. A check or review result on a stale
revision does not satisfy completion.

## Merge Cleanup

When the user says a PR is merged:

1. Verify the destination branch contains the change.
1. Confirm no uncommitted or unpushed task work is stranded.
1. Remove task worktrees that are no longer needed.
1. Delete the issue branch locally and remotely when appropriate.
1. Run `git fetch --prune`.
1. Run `git pull --ff-only` in the main repo.
1. Update the task record with final status and follow-ups.

## Plan Quality Before Assignment

Before assigning a product-derived issue, check that the issue records:

- product decisions accepted, recommended, deferred, and waived
- supported, rejected, and informational inputs
- contract propagation across DTOs, routes, CLI, SDK, OpenAPI/generated artifacts, projections, docs, and tests
- stale-authority/revalidation points for runtime, storage, materialization, Watch, auth, and eventing work
- negative proof for hidden, missing, malformed, stale, duplicate, cross-scope, and partial-failure cases

When a similar recent PR required three or more review cycles, mine that PR and convert its root-cause lanes into issue
acceptance criteria before assignment.
