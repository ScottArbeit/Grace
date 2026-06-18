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
- Write the minimum detail gate as review-prevention guidance. Predict likely Codex Code Review Bot findings, such as
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
- Do not replace the worker by locally implementing or validating code fixes from the orchestrator role.
- Include a status-reporting protocol in worker prompts. Ask the worker to maintain a temp status file outside the repo,
  for example `$env:TEMP\grace-agent-status\<issue-or-pr>-<task>.md`, with `phase`, `lastUpdate`, `changedFiles`,
  `validation`, `blockers`, and `nextStep`. Require updates before edits, before and after long validation or generation
  commands, before commit/push/handoff steps, and before the final response. Also ask for a short chat heartbeat
  roughly every five minutes while work continues.
- Require a lightweight implementation preflight before coding: acceptance criteria to prove, contract surfaces to
  update or waive, existing tests to fail or extend, adversarial cases, global options or modes, validation, touched
  paths, and any issue-owned path expansion needed before editing.
- Require a bot-prevention self-review before worker handoff. The worker should inspect the actual diff for likely
  Codex Code Review Bot findings, fix anything found before push or handoff, and report first-pass review readiness
  plus residual risks.
- Ask worker subagents to finish with a handoff as soon as their assigned implementation or fix is validated and pushed.
  By default, the orchestrator owns GitHub issue updates, PR body updates, review-comment replies, conversation
  resolution, labels, checklists, merge state, and cleanup records. The orchestrator can start the next independent
  worker from a sufficient handoff before finishing wrap-up for the previous worker when dependencies and write sets
  make that safe.
- Use Codex Code Review Bot as the blocking PR review gate. Do not spawn local review-only subagents by default. For
  high-risk slices, the orchestrator may assign a fresh pre-PR review worker before opening or updating the PR when that
  is cheaper than a likely bot/fix/re-review loop; this does not replace the bot as the blocking review gate.
- Monitor the PR body reactions: 👀 means the bot saw the latest commit and is reviewing; 👍🏻 means it found no issues.
- Inspect both top-level PR comments and inline comments attached to the bot pull request review; `gh pr view --json
  comments` alone can miss review-thread findings.
- If the bot writes findings in a top-level PR comment or inline pull-request-review comment, route the fix to a fresh
  worker subagent. After the worker hands off, the orchestrator replies to the bot comment with the fix commit and
  validation evidence, includes the prevention line from `docs/Development process.md`, resolves the conversation,
  updates `Review Status`, and waits for the next bot review.

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

Default commands:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
npx --yes markdownlint-cli2 "**/*.md"
git diff --check
```

Run `-Full` for Aspire integration, emulators, storage, Cosmos DB, Service Bus, Redis, runtime delivery, or
cross-service behavior.

Use the validation ladder from `docs/Development process.md`: run targeted Fantomas formatting or checks before
validation for touched F# files, run freshness checks when needed, then choose exactly one final build/test gate. If
`validate -Fast` or `validate -Full` will run, do not also ask workers to routinely run project-specific
`dotnet build` plus `dotnet test --no-build`; `validate` is the final build/test gate.

Focused project build/test remains appropriate for RED evidence, failure diagnosis, skipped-validate workflows, tests
outside the selected validate profile, or explicitly focused-only issues. Freshness/update workers follow the same
rule: formatting or freshness checks first, then one final build/test gate.

Before the Grace completion review gate, update the branch against its required base: current `origin/main` for
standalone non-epic issue branches, current `origin/epic/...` for sub-issue branches targeting an epic integration
branch, and current `origin/main` for the final epic-to-`main` PR. Verify ahead/behind, verify the scoped diff and that
no unexpected deletions are present, run the chosen validation gate, then wait for Codex Code Review Bot on the
refreshed PR head. A bot reaction or comment on a stale commit does not satisfy completion.

## Merge Cleanup

When the user says a PR is merged:

1. Verify the destination branch contains the change.
1. Confirm no uncommitted or unpushed task work is stranded.
1. Remove task worktrees that are no longer needed.
1. Delete the issue branch locally and remotely when appropriate.
1. Run `git fetch --prune`.
1. Run `git pull --ff-only` in the main repo.
1. Update the task record with final status and follow-ups.
