# Grace Development Process

Grace's development process is issue-owned, evidence-heavy, and branch/worktree based. GitHub issues and pull requests
are the active coordination surfaces for implementation work. Normal development proceeds from a scoped issue to an
issue-owned branch and worktree, then through focused validation, review, and merge.

Use this process for non-trivial implementation, workflow, documentation, infrastructure, and agent-assisted changes.
For small typo fixes or tiny docs corrections, keep the spirit of the process but scale the ceremony down.

## Core Principles

- Start from the repo-local instructions. Read the root `AGENTS.md`, then the closest project `AGENTS.md`.
- For tracked implementation work, keep one visible task record: a GitHub issue created from the Grace agent task
  template.
- When the user says `Plan <work item>`, plan the work in chat. Create a GitHub issue only when the user explicitly asks
  for one, asks to start tracked implementation, or otherwise requests tracker setup.
- Declare the intended write set before editing. If the write set grows, update the task record before editing the new
  paths.
- Prefer vertical slices over broad horizontal phases.
- Add or update focused tests for behavior changes.
- Run the fastest meaningful validation first, then broader validation when risk or shared surfaces justify it.
- Commit after each completed slice so review scope stays clear.
- Keep docs, README guidance, and nearby `AGENTS.md` files aligned with behavior and workflow changes.

## Task Record

Before editing files, create or confirm a GitHub issue using the Grace agent task template. The issue is the work
contract for the branch, worktree, validation, and pull request.

The issue should include this information:

```markdown
Objective:
- One concrete behavior, docs, workflow, or infrastructure slice.

Context and evidence:
- Logs, files, symptoms, prior PRs, design notes, or commands already run.

Owned paths:
- Files or directories this task may edit.

Forbidden or sensitive paths:
- Files or directories that require explicit expansion before editing.

Risk surfaces:
- Auth or secrets
- Storage, Cosmos DB, Service Bus, Redis, or Aspire
- CLI public contract
- Server or API contract
- Orleans actor behavior
- SDK or client contract
- Docs or workflow
- No special risk expected

Validation:
- Focused command:
- Fast repo gate:
- Full or Aspire gate, if needed:
- Manual verification, if needed:

Definition of done:
- Behavior changed
- Tests or docs updated
- Coding work reviewed by a local review-only subagent, using its dedicated Code Review capability when available, with
  no issues remaining
- Ready-for-review pull request opened and linked
- Validation recorded
- Review evidence prepared
- Follow-ups named
```

## Workspace

After the GitHub issue exists, claim it before editing and create an issue-owned branch and worktree from the latest
`origin/main`.

Post a claim comment before editing:

```markdown
## Claimed

**Agent:** <agent name or run id>

**Branch:** `agent/<issue-number>-<slug>`

### Planned Write Set

- <path 1>
- <path 2>

### Forbidden Paths Acknowledged

- <path A>
- <path B>

### Validation Planned

- <focused tests>
- <fast/full baseline or reason it may be deferred to CI>

### Validation Profile

_<profile from docs/Development process.md>_

### Conflict Score

**<0-3>** - <reason>
```

Recommended branch name:

```text
agent/<issue-number>-<short-slug>
```

Recommended worktree shape:

```powershell
git fetch origin
git worktree add ../Grace-gh-184 -b agent/184-short-slug origin/main
Set-Location ../Grace-gh-184
```

Always inspect the current state before editing:

```powershell
git status --short --branch
```

If unrelated changes already exist, leave them alone. If they affect the task, work with them instead of reverting them.
If the task must expand beyond the issue's owned paths, comment on the issue before editing the new paths.

## Validation Profiles

Choose a profile before changing code.

- `docs-only`: Markdown, HTML, guidance, or static documentation. Validate with MarkdownLint, rendered output checks, or
  `git diff --check` as appropriate.
- `domain-contract`: Types, DTOs, validators, serializers, hashes, or shared helpers. Add focused tests in the matching
  `Grace.Types.Tests`, `Grace.Shared` test surface, or nearby test project.
- `cli-command`: Grace CLI command behavior. Add or update focused tests in `Grace.CLI.Tests`.
- `server-api`: HTTP handlers, server services, auth, persistence boundaries, or API contracts. Add or update focused
  tests in `Grace.Server.Tests`.
- `actor-workflow`: Orleans actor behavior. Prefer server-surface integration tests unless the project-specific guide
  calls for actor-level tests.
- `sdk-client`: SDK surface or client contract changes. Add or update focused SDK tests or server contract tests.
- `deployment-runtime`: Aspire, emulators, Docker, Azure resources, scripts, or runtime configuration. Pair parser or
  script checks with full validation or live evidence when needed.

## Slice Loop

For behavior-changing work, use this loop:

```text
Task record -> validation profile -> public boundary -> RED -> GREEN -> REFACTOR -> focused validation -> commit
```

For each slice:

1. Add or update one focused test that names the behavior, invariant, transition, command, or API contract.
2. Run the focused command and confirm the failure is meaningful.
3. Implement the smallest change that makes the behavior pass.
4. Run the focused command again.
5. Refactor names, module boundaries, builders, or duplication while tests are green.
6. Run focused validation after the refactor.
7. Commit the completed slice with a clear message.

For docs-only work, replace the RED step with a focused validation target such as MarkdownLint, rendered HTML review,
YAML parsing, or `git diff --check`.

## Required Agent Code Review

A coding task is not complete until the implementing agent runs a local review-only subagent after the implementation
slice has been validated and committed. If the subagent launcher exposes a dedicated Code Review mode, skill, command,
or capability, select it explicitly for that subagent. Do not assume that a generic prompt asking a model to act like a
reviewer automatically activates the dedicated Code Review capability.

Do not use GitHub `@codex review`, automatic Codex pull request review, or another external pull-request review bot for
this completion gate. Those flows are intentionally outside the Grace development loop because they are too slow for
each review-fix turn.

The review subagent must use a medium-sized, lower-cost model with high reasoning effort. Use a model class comparable
to `gpt-5.4-mini` with high reasoning, or the nearest equivalent available in the active model provider. The subagent
prompt must say to act strictly as a code reviewer, not edit files, inspect the committed diff and relevant surrounding
code/tests, report only actionable issues, and clearly say when there are no issues.

The review loop is blocking:

1. Run the local review-only subagent against the committed task diff, using its dedicated Code Review capability when
   the subagent environment exposes one.
2. If the review finds issues, address them in the issue-owned branch/worktree.
3. Re-run focused validation for the changed behavior or docs, and broader validation when the fix touches shared or
   risky surfaces.
4. Commit the review fix.
5. If a pull request already exists, add a new standalone pull request comment for the review fix using the
   [Review/Fix comment template](#reviewfix-comment-template). The comment must make the high-level outcome easy to
   scan before the detailed issue and fix text. Do not add review-fix notes to the pull request body. If the pull
   request does not exist yet, add the standalone comment immediately after opening the pull request.
6. Run another local review-only subagent pass against the updated committed diff, again using the dedicated Code Review
   capability when the subagent environment exposes one.
7. Repeat the loop until the review reports no issues.

Only after the local review-only subagent reports no issues can the task continue toward pull request creation, handoff,
merge readiness, or any other completion step. Record whether a dedicated subagent Code Review capability was available,
the final no-issues review result, and validation evidence in the task record or pull request.

### Review/Fix Comment Template

Use this Markdown structure for each standalone pull request comment created after a review issue is fixed. Keep the
top section short and scannable; put detailed evidence below it.

```markdown
## Review/Fix: <short issue title>

**Status:** Fixed in `<commit-sha>`
**Review source:** Local review-only subagent
**Validation:** <command or check result>

### Summary

_One or two sentences explaining the review issue and the fix at a high level._

### Review Issue

<Describe the actionable issue the reviewer found. Include file/line references when available.>

### Fix

<Describe the code or docs change that addressed the issue. Include the fix commit and important files changed.>

### Validation

- `<focused command>`: <result>
- `<broader command, if any>`: <result or skipped reason>
```

## Validation Commands

Use the local scripts for repository validation:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
```

Use `-Fast` for the normal development loop. Use `-Full` when the change touches Aspire integration coverage, emulators,
storage, Cosmos DB, Service Bus, Redis, cross-service behavior, or deployment/runtime behavior.

If running commands manually, the high-level fallback is:

```powershell
dotnet build --configuration Release
dotnet test --no-build
```

For Markdown changes, use:

```powershell
npx --yes markdownlint-cli2 "**/*.md"
git diff --check
```

If validation is skipped, record exactly what was skipped and why in the task record or pull request.

## Documentation Expectations

Update documentation in the same slice when a change affects:

- public commands, options, or environment variables
- repository structure or project ownership
- build, test, validation, or deployment workflow
- public APIs, SDK behavior, or CLI behavior
- authentication, authorization, secrets, storage, or runtime configuration
- agent guidance that future maintainers need to inherit

Keep documentation close to the behavior it describes. Use the root `README.md` for the first-stop roadmap,
`CONTRIBUTING.md` for contributor workflow, root `AGENTS.md` for repo-wide agent rules, and project `AGENTS.md` files
for project-specific conventions.

## Review And Integration

Before opening or updating a pull request, include:

- the linked GitHub issue
- summary of changed behavior
- touched paths and any write-set expansion
- focused validation run
- broader validation run, or skipped-validation reason
- review path used: local review-only subagent, including whether a dedicated Code Review capability was available
- final no-issues code review result
- standalone, templated Markdown pull request comments for each review issue that required a fix, including the issue,
  fix, fix commit, and validation; do not put review-fix notes in the pull request body
- docs impact
- residual risk
- rollback or recovery notes when the change touches runtime or data
- useful AI prompts used for diagnosis or implementation, when contributing externally

Open normal ready-for-review pull requests for Grace implementation work. Do not open draft pull requests unless the
maintainer explicitly asks for a draft.

Grace's product model uses promotion candidates, queues, gates, attestations, and review reports. Today's repository
still uses normal GitHub pull requests, but changes should be prepared so they are easy to audit in either system.

## Review Feedback

When the user asks an agent to address a code review comment, review comment, PR feedback, or similar wording, treat the
request as a complete review-thread workflow.

The agent should:

1. Inspect the GitHub review thread or PR feedback directly and separate actionable feedback from informational comments.
2. Evaluate whether the feedback is correct and identify the smallest appropriate fix, or state why no code change is
   needed.
3. Make the fix in the issue-owned branch/worktree and keep the change traceable to the review thread.
4. Run focused validation for the changed behavior or docs, and broader validation when the feedback touches shared or
   risky surfaces.
5. Commit the fix and push the branch.
6. Reply to the GitHub review comment with the outcome, changed commit, and validation evidence.
7. Resolve the GitHub conversation after the feedback has been satisfied.

If the comment is ambiguous, conflicts with another requirement, or would cause a behavioral regression, ask for
clarification or reply with the trade-off instead of resolving the thread prematurely.

## Cleanup

After merge or promotion:

1. Verify the destination branch or reference contains the change.
2. Confirm no uncommitted or unpushed work is stranded in the task workspace.
3. Remove task worktrees that are no longer needed and delete the issue branch locally and remotely.
4. Run `git fetch --prune` and `git pull --ff-only` in the local repo so `main` is up to date.
5. Update the task record with final status and follow-ups.
6. Leave unrelated local changes untouched.

When the user says "PR is merged" or uses equivalent wording, treat that as a request to perform this cleanup sequence.
