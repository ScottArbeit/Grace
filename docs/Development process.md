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

## Task Records

Before editing files, create or confirm a GitHub issue using the Grace agent task template. The issue is the work
contract for the branch, worktree, validation, and pull request.

For single-slice implementation work, that issue can be the whole task record. For multi-step implementation plans, use
an epic parent issue plus one linked sub-issue for each implementation step. The parent issue owns the overall goal,
dependency map, and integration status. Each sub-issue owns one implementation slice, branch/worktree, validation path,
review loop, and pull request. When creating the epic and sub-issues, assign each sub-issue's parent issue relationship
to the epic in GitHub Relationships.

The parent issue for a multi-step implementation plan must include a DAG that shows:

- each implementation step as a node
- dependencies between steps
- steps that can run in parallel
- the expected integration order when parallel branches converge

The parent issue must also include a sub-issue checklist. As sub-issues complete, update that checklist so completed
sub-issues are checked.

Keep each sub-issue small and clear enough that an implementation agent can reasonably succeed from the issue body
alone. If a step needs hidden project knowledge to succeed, split it smaller or add the missing context before assigning
it.

Before assigning or starting a sub-issue, apply this minimum detail gate:

- Invariant tuple: the actor, route, command, DTO, stored object, or workflow state that must remain true; the identity
  dimensions that define "same" versus "stale"; and the durable source of truth.
- Forbidden implementation shapes: shortcuts that would satisfy the happy path while violating the design, security
  boundary, durability model, or public contract.
- Expected tests: focused positive, negative, regression, and boundary tests that must exist before the worker can call
  the slice complete.
- High-risk adversarial examples: stale IDs, cross-scope objects, reordered events, retries, duplicate requests, mutable
  config, cancellation, redaction, or other edge cases likely to trick a shallow implementation.

Include the behavior to change, relevant context and evidence, owned paths, forbidden or sensitive paths, validation
commands, docs impact, and the definition of done.

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
- Coding and review work completed through implementation and review-only subagents, with the main agent acting as
  orchestrator
- Fresh local review-only sibling subagent reported no issues
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

## Required Agent Orchestration And Review

When acting as the main agent for a tracked implementation, stay in the orchestrator role. The main agent owns issue
coordination, DAG sequencing, prompts, review ledgers, pull request updates, CI/merge status, docs/process updates,
review/fix routing, and final integration evidence. The main orchestrator must delegate all coding and fixing tasks to
worker subagents and must not implement, repair, inspect or validate code fixes as a substitute for the worker, or
commit code changes locally. Code review work must be delegated to fresh local review-only sibling subagents.

If an earlier worker thread is lost, compacted away, leaves uncommitted work, or otherwise cannot be resumed, the main
orchestrator must assign the continuation to a fresh worker subagent. The continuation prompt must include the existing
worktree and branch, current git status, prior objective, owned and forbidden paths, validation requirements, and any
review findings or review-ledger notes already recorded. The main orchestrator may inspect enough metadata to route the
work safely, but it must not take over code implementation or code-fix validation itself.

After the first coding subagent that works on the issue commits and pushes the new branch to origin, the orchestrator
must open a normal ready-for-review pull request. The pull request can remain open while the step is still in progress.
From that point on, review findings, review fixes, validation evidence, and "Reviewed And OK" notes should be recorded
as standalone pull request comments, not only as issue comments. Use the issue for claim, planning, parent/epic
coordination, and pre-PR evidence; use the pull request as the durable code-review ledger once it exists.
If the first code review for a newly opened pull request reports no issues, still add a standalone pull request comment
containing the review output. A clean review is evidence, and it belongs on the pull request with the code it reviewed.
Whenever the orchestrator adds or updates code-review comments on a pull request, it must update the pull request
body's `Review Status` section in the same turn. That section should stay high-level: reviews run, current open
findings, fix commits already pushed, final no-issues review status, and links to detailed standalone review/fix
comments.

A coding task is not complete until a parent/orchestrator thread runs a fresh local review-only sibling subagent after
the implementation slice has been validated and committed. The review agent must be a sibling of the implementation
agent, not a nested `codex review` process launched from inside an existing agent or subagent.

The implementation subagent must stop after committing, validating, and pushing the slice branch, then return a
[Ready For Review handoff](#ready-for-review-handoff) to the parent/orchestrator thread. The parent/orchestrator is
responsible for spawning the fresh review-only sibling subagent, collecting its findings, and sending actionable issues
back to an implementation subagent for fixes. After every review-only pass, the parent/orchestrator must persist the
review report to the pull request when one exists, or to the GitHub issue before the first pull request exists, before
starting the next implementation or review pass. The persisted report must include both actionable findings and the
"Reviewed And OK" notes.

If the subagent launcher directly exposes a dedicated Code Review mode, skill, command, or capability, select it
explicitly for the review-only sibling subagent. Do not assume that a generic prompt asking a model to act like a
reviewer automatically activates the dedicated Code Review capability.

Do not run `codex review` through the shell from inside an agent or subagent. Do not use GitHub `@codex review`,
automatic Codex pull request review, or another external pull-request review bot for this completion gate. Those flows
are intentionally outside the Grace development loop because they are too slow for each review-fix turn.

The subagent prompt must say to act strictly as a code reviewer, not edit files, inspect the committed diff and relevant
surrounding code/tests, report only actionable issues, and clearly say when there are no issues.

Code review turns can legitimately take several minutes. After spawning a review-only sibling subagent, allow it to run
for up to 10 minutes before analyzing whether it is stalled or still making useful progress. Do not interrupt or replace
the review just because it is quiet for a few minutes. The review subagent must report back when it is done so the
parent/orchestrator can continue the workflow without guessing about the review state. That final report must also name
the plausible issues the reviewer checked and found not to be problems, so the next review subagent does not re-check
the same concern without a clear reason.

The review loop is blocking:

1. From the parent/orchestrator thread, spawn a fresh local review-only sibling subagent against the committed task diff,
   using a dedicated Code Review capability when the subagent launcher directly exposes one.
2. Give the review subagent up to 10 minutes to complete before deciding whether to inspect progress, redirect it, or
   replace it. If it is still making useful progress, continue waiting.
3. Require the review subagent to send a final report when it is done: either actionable findings with file/line
   references where possible, or a clear no-issues result. The final report must include a short "Reviewed And OK"
   section with brief bullets for non-issues the reviewer explicitly checked, especially concerns raised by prior
   review passes.
4. Persist the full review report to the pull request when one exists, or to the issue before the first pull request
   exists. If another review pass will run later, copy the prior "Reviewed And OK" notes into that review prompt and
   tell the reviewer to treat them as already-reviewed unless the new diff affects those areas. This applies even when
   the first pull request review finds no issues.
   Also update the pull request body's `Review Status` section when the pull request already exists.
5. If the review finds a missing acceptance-criterion class, repeated trap, or issue-template gap that could affect
   active future workers, amend the active future issues or templates before spawning parallel workers. Preserve issue
   history by appending an addendum unless replacing stale text is clearer and safe.
6. If the review finds issues, send them to an implementation subagent to address in the issue-owned branch/worktree.
7. The implementation subagent re-runs focused validation for the changed behavior or docs, plus broader validation when
   the fix touches shared or risky surfaces.
8. The implementation subagent commits and pushes the review fix, then returns a new Ready For Review handoff.
9. Add a new standalone pull request comment for each review fix using the
   [Review/Fix comment template](#reviewfix-comment-template). The comment must make the high-level outcome easy to
   scan before the detailed issue and fix text. Do not add review-fix notes to the pull request body; keep the body to a
   high-level `Review Status` summary and links to detailed comments.
10. From the parent/orchestrator thread, spawn another fresh review-only sibling subagent pass against the updated
   committed diff, again using a dedicated Code Review capability when the subagent launcher directly exposes one.
11. Repeat the loop until the review reports no issues.

Only after a fresh local review-only sibling subagent reports no issues can the task continue toward merge readiness,
handoff, or any other completion step. Record whether a dedicated subagent Code Review capability was available, the
final no-issues review result, and validation evidence in the pull request.

### Ready For Review Handoff

When the implementation agent is itself a subagent, it must not attempt to satisfy the review gate by running `codex`
or spawning nested review work. Instead, it must return this handoff to the parent/orchestrator thread:

```markdown
## Ready For Review

**Worktree:** `<absolute path>`
**Branch:** `<branch-name>`
**Commit:** `<commit-sha> <commit subject>`
**Diff:** `<base>..<head>`
**Remote:** `<origin branch or PR URL, if already available>`

### Validation

- `<focused command>`: <result>
- `<broader command, if any>`: <result or skipped reason>

### Review Request

Please spawn a fresh local review-only sibling subagent. Use the subagent launcher's dedicated Code Review capability if
it is directly exposed. Do not run `codex review` through the shell. Allow the review subagent to run for up to 10
minutes before analyzing whether it is stalled. The review subagent must report back when complete with either
actionable findings or a clear no-issues result. The report must include a short "Reviewed And OK" section naming
plausible concerns it checked that were not problems, especially anything checked by prior review passes.
```

If the sibling review finds issues, the parent/orchestrator sends those findings to an implementation subagent. The
implementation subagent addresses the findings, validates, commits and pushes the fixes, and returns a new Ready For
Review handoff. The parent/orchestrator posts any required standalone pull request comments and then spawns another
fresh review-only sibling subagent.

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

### Review Report Shape

Review-only subagents should keep final reports concise and use this shape. The "Reviewed And OK" section is required
even when issues are found; it should prevent repeated re-checking, not pad the report.

```markdown
## Code Review Result

**Status:** <No issues | Issues found>
**Diff reviewed:** `<base>..<head>`

### Findings

- <Actionable issue with file/line reference, or "No actionable issues.">

### Reviewed And OK

- <Concern checked and why it is not a problem.>
- <Prior-review concern checked and still OK, if applicable.>
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

For F# changes, run Fantomas formatting or targeted Fantomas checks before build and test validation. The intended
order is:

1. Apply the code change.
2. Run Fantomas on the touched files, or run the repo-standard recursive Fantomas command when the edit is broad.
3. Run build and tests.
4. Run `git diff --check`.

Avoid running the full test suite before formatting, then discovering Fantomas rewrote files and forcing another
build/test cycle.

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

When opening or updating a pull request, include the evidence available at that point and keep adding standalone
comments as the review loop continues:

- the linked GitHub issue
- summary of changed behavior
- touched paths and any write-set expansion
- focused validation run
- broader validation run, or skipped-validation reason
- implementation and review path used, including the implementation subagent, fresh local review-only sibling subagent,
  and whether a dedicated Code Review capability was available
- final no-issues code review result
- "Reviewed And OK" notes from the final review pass, especially prior concerns that were rechecked and found safe
- standalone, templated Markdown pull request comments for each review issue that required a fix, including the issue,
  fix, fix commit, and validation; do not put review-fix notes in the pull request body
- a `Review Status` section that summarizes the current review/fix state and links to detailed review/fix comments
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
