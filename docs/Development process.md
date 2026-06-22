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
- Include the purpose behind each feature or implementation change. Plans, issue bodies, and pull request bodies should
  explain why the work benefits Grace and its users so implementation agents can make better decisions when details are
  incomplete.
- Use the existing issue details to prevent likely Codex Code Review Bot findings before the pull request exists. This
  means sharper invariants, forbidden implementation shapes, adversarial cases, and test evidence rather than more
  issue-template ceremony.

## Task Records

Before editing files, create or confirm a GitHub issue using the Grace agent task template. The issue is the work
contract for the branch, worktree, validation, and pull request.

For single-slice implementation work, that issue can be the whole task record. For multi-step implementation plans, use
an epic parent issue plus one linked sub-issue for each implementation step. The parent issue owns the overall goal,
dependency map, and integration status. Each sub-issue owns one implementation slice, branch/worktree, validation path,
review loop, and pull request. When creating the epic and sub-issues, assign each sub-issue's parent issue relationship
to the epic in GitHub Relationships.

When planning a feature or epic, state why the change matters before decomposing the work. Connect the work to the
benefit for Grace and its users: improved trust, safer operations, clearer contracts, faster workflows, lower operator
risk, better product fit, or another task-specific outcome. Carry that purpose into the parent epic, child issues, and
pull request bodies. This context lets implementation agents choose better local tradeoffs when the plan leaves a gap
or an acceptance criterion is ambiguous.

Create the parent epic issue and child issues with `gh issue create --body-file`, using issue bodies written as separate
Markdown files in a temporary directory. Prefer one small `Set-Content` or equivalent file-write command per issue body
over one giant inline PowerShell script or command that embeds every Markdown body. This avoids Windows command-line
length limits and keeps failed issue creation recoverable.

Create the issues first. After GitHub returns the real child issue numbers, patch the parent epic body so its checklist
and DAG reference those actual numbers. Then create the native GitHub parent/child relationships with the GitHub GraphQL
`addSubIssue` mutation for each child. The native relationship is the GraphQL part; REST issue links, body checklists,
and cross-reference comments are useful traceability but are not a substitute for the native parent relationship.

Use this mutation shape:

```graphql
mutation($parent: ID!, $child: ID!) {
  addSubIssue(input: { issueId: $parent, subIssueId: $child, replaceParent: true }) {
    issue { number title }
    subIssue { number title parent { number title } }
  }
}
```

`issueId` is the parent epic issue node ID. `subIssueId` is the child issue node ID. Use variables instead of embedding
node IDs in the query string. This PowerShell-friendly shape keeps the GraphQL text and each issue body in temporary
files:

```powershell
$temp = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "grace-epic-$([guid]::NewGuid())")
$parentBody = Join-Path $temp.FullName "parent.md"
$childBody = Join-Path $temp.FullName "child-1.md"
$addSubIssueQuery = Join-Path $temp.FullName "add-subissue.graphql"

Set-Content -LiteralPath $parentBody -Value @'
# Epic: <short title>

## Checklist

- [ ] Child issue will be patched in after creation.
'@

Set-Content -LiteralPath $childBody -Value @'
# <child title>

## Objective

- <one implementation slice>
'@

Set-Content -LiteralPath $addSubIssueQuery -Value @'
mutation($parent: ID!, $child: ID!) {
  addSubIssue(input: { issueId: $parent, subIssueId: $child, replaceParent: true }) {
    issue { number title }
    subIssue { number title parent { number title } }
  }
}
'@

$parentUrl = gh issue create --title "Epic: <short title>" --body-file $parentBody
$childUrl = gh issue create --title "<child title>" --body-file $childBody

$parentNumber = [int]([regex]::Match($parentUrl, '/issues/(\d+)$').Groups[1].Value)
$childNumber = [int]([regex]::Match($childUrl, '/issues/(\d+)$').Groups[1].Value)

$updatedParentBody = Join-Path $temp.FullName "parent-updated.md"
(Get-Content -Raw -LiteralPath $parentBody).Replace(
  "Child issue will be patched in after creation.",
  "#$childNumber - <child title>"
) | Set-Content -LiteralPath $updatedParentBody
gh issue edit $parentNumber --body-file $updatedParentBody

$parentNodeId = gh issue view $parentNumber --json id --jq .id
$childNodeId = gh issue view $childNumber --json id --jq .id

gh api graphql `
  -f query="$(Get-Content -Raw -Path $addSubIssueQuery)" `
  -F parent="$parentNodeId" `
  -F child="$childNodeId"
```

After adding relationships, verify both the epic's `subIssues.totalCount` and each child's `parent.number`:

```graphql
query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    issue(number: $number) {
      number
      title
      subIssues(first: 50) {
        totalCount
        nodes {
          number
          title
          parent { number title }
        }
      }
    }
  }
}
```

PowerShell example:

```powershell
$verifyQuery = Join-Path $temp.FullName "verify-subissues.graphql"
Set-Content -LiteralPath $verifyQuery -Value @'
query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    issue(number: $number) {
      number
      title
      subIssues(first: 50) {
        totalCount
        nodes {
          number
          title
          parent { number title }
        }
      }
    }
  }
}
'@

gh api graphql `
  -f query="$(Get-Content -Raw -Path $verifyQuery)" `
  -F owner="<owner>" `
  -F name="<repo>" `
  -F number="$parentNumber"
```

The parent issue for a multi-step implementation plan must include a DAG that shows:

- each implementation step as a node
- dependencies between steps
- steps that can run in parallel
- the expected integration order when parallel branches converge

Treat that product DAG as necessary but not sufficient for parallel execution. A step can be behaviorally independent
while still creating predictable merge churn. Before assigning parallel branches, compare the expected write sets:

- Parallelize only when branches are disjoint enough to avoid routine conflict resolution.
- Serialize or merge-queue branches that touch shared project files such as `*.fsproj`, `Startup.Server.fs`, or the
  same test/helper files.
- For broad waves, consider a preparatory compile-item or file-scaffold slice, then let later branches edit separate
  files.

### Epic Merge Strategy

When implementing an epic, always use an explicit epic integration branch and record that branch in the parent issue.
Do not use direct-to-`main` epic slices.

Create `epic/<parent-issue>-<short-slug>` from current `origin/main`. Sub-issue branches and worktrees start from the
current `origin/epic/<parent-issue>-<short-slug>`, and sub-issue pull requests target the epic branch. The epic branch
is an integration branch, not a production deployment branch. The final ready-for-review pull request from the epic
branch to `main` is the production release candidate for the epic.

When using an epic integration branch:

- Keep the parent issue DAG, checklist, and merge strategy clear about which sub-issues target the epic branch.
- Keep the epic branch refreshed from `origin/main`, especially before later sub-issue waves and before the final
  epic-to-`main` pull request.
- Ensure CI validates pull requests targeting `epic/**`, or record the CI gap and required local validation in the
  parent issue before assigning workers.
- Treat each sub-issue as complete when it is reviewed, validated, merged to the epic branch, and cleaned up.
- Treat the epic as complete only after the final epic-to-`main` pull request is reviewed, validated against current
  `origin/main`, merged to `main`, and cleaned up.
- Make sure every sub-issue pull request links to its sub-issue in the pull request body. Use non-closing wording for
  pull requests that target the epic branch, then close the sub-issue manually after merge when the slice is complete.

The parent issue must also include a sub-issue checklist. As sub-issues complete, update that checklist so completed
sub-issues are checked.

Keep each sub-issue small and clear enough that an implementation agent can reasonably implement it from the issue body
alone. If a step needs hidden project knowledge to succeed, split it smaller or add the missing context before assigning
it. For behavior-changing work, objective, owned paths, and validation commands are not enough. The issue must also say
what must remain true, what shortcuts are forbidden, what evidence would catch a false positive, and which contract or
runtime surfaces must be updated or explicitly waived.

Before assigning, claiming, or starting a coding issue, apply this minimum detail gate:

- Invariant tuple: the actor, route, command, DTO, stored object, or workflow state that must remain true; the identity
  dimensions that define "same" versus "stale"; and the durable source of truth.
- Forbidden implementation shapes: shortcuts that would satisfy the happy path while violating the design, security
  boundary, durability model, or public contract.
- Expected tests: focused positive, negative, regression, and boundary tests that must exist before the worker can call
  the slice complete.
- High-risk adversarial examples: stale IDs, cross-scope objects, reordered events, retries, duplicate requests, mutable
  config, cancellation, redaction, or other edge cases likely to trick a shallow implementation.
- Selected risk-surface traps from the checklist below.
- Explicit "not applicable" waivers with reasons for skipped gate items or risk surfaces.

Write the minimum detail gate as review-prevention guidance. The issue should predict the implementation shortcuts,
weak tests, contract drift, stale identity assumptions, authorization mistakes, async or cancellation gaps, stale docs,
generated-artifact omissions, or validation gaps that Codex Code Review Bot is likely to flag if the worker misses them.
Prefer a few task-specific warnings over copying broad checklists. This is a quality bar for the existing issue fields,
not a new required section.

If review finds a missing class of acceptance criterion, adversarial case, contract propagation, or validation evidence,
update active and future sibling issues before assigning more workers. Preserve issue history by appending an addendum
unless replacing stale text is clearer and safe.

### Risk-Surface Trap Checklist

Use this checklist to choose the task-local traps that belong in the minimum detail gate. Not every row applies to every
issue, but every selected or skipped row should be clear enough that a worker and reviewer can see what must be proven.

- Proof/test work: identify the false-positive test review is likely to catch. Prove the assertion would fail on
  regression, not just execute the path.
- DTO/contract work: check JSON shape, MessagePack or other serialization shape, OpenAPI component, aggregate OpenAPI,
  generated-client impact, SDK/facade impact, docs impact, and compatibility posture.
- CLI work: check `--output Json`, `--select`, `--schema`, `--examples`, stdout cleanliness, stderr/progress behavior,
  exit-code behavior, and whether global options accidentally skip or duplicate side effects.
- Server/API work: specify ordering for parse, null/blank validation, domain validation, authorization,
  resource/path authorization, materialization, mutation/query, and envelope/error serialization.
- Storage/materialization work: cover missing/corrupt content, size limits, hash/length mismatch, cancellation,
  retained bytes, compressed/uncompressed paths, and target-vs-ancestor failure semantics.
- Async/runtime work: prove ordering, retry, dedupe, cancellation, idempotency, and observability through deterministic
  signals, not arbitrary sleeps.
- Algorithm work: include adversarial input/output examples for tie-breaking, small ranges, boundary conditions,
  budgets, pathological runtime cases, and excluded or filtered data.
- History/traversal work: cover target reference windowing, parent links, `BasedOn`, filtered-vs-traversed history,
  missing/unauthorized/unreadable ancestors, loops, and traversal budgets.
- Final audit/docs work: list exact commands/checks rerun, branch/head used, stale evidence rejected, docs/examples
  verified against runtime behavior, and explicit deferred or residual risks.

Include the behavior to change, relevant context and evidence, owned paths, forbidden or sensitive paths, validation
commands, docs impact, and the definition of done.

The issue should include this information:

```markdown
Objective:
- One concrete behavior, docs, workflow, or infrastructure slice.

Why this matters:
- How this benefits Grace and its users, and what better decision-making context it gives the implementer.

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

Minimum detail gate:
- Invariant tuple:
- Forbidden implementation shapes:
- Expected tests:
  - positive:
  - negative:
  - regression:
  - boundary:
- High-risk adversarial examples:
- Selected risk-surface traps:
- Explicit N/A waivers with reasons:

Validation:
- Focused command:
- Fast repo gate:
- Full or Aspire gate, if needed:
- Manual verification, if needed:

Definition of done:
- Behavior changed
- Tests or docs updated
- Coding and fix work completed through implementation subagents, with the main agent acting as orchestrator
- Worker completed a bot-prevention self-review over the actual diff before handoff
- Codex Code Review Bot reviewed the latest PR commit and either reported no issues with a 👍🏻 reaction on the PR body
  or all bot comments were fixed, answered, resolved, and followed by a no-issues bot review
- Ready-for-review pull request opened and linked
- Validation recorded
- Review evidence prepared
- Follow-ups named
```

## Workspace

After the GitHub issue exists, claim it before editing, assign it to the authenticated GitHub user, and create an
issue-owned branch and worktree from the selected base:

- standalone non-epic issue: use the latest `origin/main`
- sub-issue under the required epic integration branch: use the current
  `origin/epic/<parent-issue>-<short-slug>`

Post a claim comment and assign the issue to the authenticated GitHub user before editing:

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

Epic integration branch shape:

```powershell
git fetch origin
git worktree add ../Grace-epic-184 -b epic/184-short-slug origin/main
git push -u origin epic/184-short-slug
git worktree add ../Grace-gh-185 -b agent/185-short-slug origin/epic/184-short-slug
Set-Location ../Grace-gh-185
```

Always inspect the current state before editing:

```powershell
git status --short --branch
```

When a task assigns a worktree different from the thread workspace root, every `apply_patch` filename must be an
absolute path under the assigned worktree. After the first patch, verify `git status --short --branch` in both the
assigned worktree and the workspace root.

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
commit code changes locally. Grace relies on Codex Code Review Bot for the blocking pull request code-review gate, not
locally spawned review-only subagents.

If an earlier worker thread is lost, compacted away, leaves uncommitted work, or otherwise cannot be resumed, the main
orchestrator must assign the continuation to a fresh worker subagent. The continuation prompt must include the existing
worktree and branch, current git status, prior objective, owned and forbidden paths, validation requirements, and any
review findings or review-ledger notes already recorded. The main orchestrator may inspect enough metadata to route the
work safely, but it must not take over code implementation or code-fix validation itself.

Every worker prompt should include a status-reporting protocol so the orchestrator does not have to infer whether the
worker is active, blocked, validating, or ready for review. For Grace work, ask the worker to create or update a temp
status file outside the repository, such as `$env:TEMP\grace-agent-status\<issue-or-pr>-<task>.md`, with these fields:

- `phase`: current work phase, such as orienting, patching, focused validation, full validation, handoff, or blocked
- `lastUpdate`: timestamp for the latest status write
- `changedFiles`: intended or actual file groups touched
- `validation`: commands run, running, passed, failed, or intentionally skipped
- `blockers`: concrete blockers or an empty list
- `nextStep`: the next action the worker intends to take

Require the worker to update that file before code edits, before and after long validation or generation commands,
before commit/push/handoff steps, and before its final response. Also require a short chat heartbeat roughly every
five minutes while still working. The heartbeat should be a status update, not a request for attention, unless the worker
is blocked. The orchestrator may read the status file and thread output to monitor progress, and should avoid
interrupting an active worker only to ask for status.

Agents must never sleep or poll for more than 120 seconds in one command. This applies to `Start-Sleep`, `wait_agent`,
long-polling commands, watch loops, and tool waits. Use repeated shorter checks instead, and update the status file
between checks when monitoring long-running validation, generation, CI, or review activity.

Before editing files, the implementation worker should post or include a lightweight preflight after reading the issue:

```markdown
## Implementation Preflight

- Acceptance criteria I will prove:
- Contract surfaces I must update or explicitly waive:
- Existing tests I expect to fail or extend:
- Adversarial cases I will cover:
- Global options or modes that could change behavior:
- Validation I will run:
- Paths I will touch:
- Issue-owned path expansion needed before editing:
```

Keep this preflight short. Its job is to catch missing acceptance criteria, contract surfaces, adversarial examples,
mode interactions, validation gaps, and path-lease expansion before code changes start.

Before handoff, the worker must perform a bot-prevention self-review over the actual diff. This is part of
implementation done, not a separate review phase. The worker should look for likely Codex Code Review Bot findings in:

- correctness and edge cases
- missing, weak, or false-positive tests
- authorization, scope, identity, or materialization ordering
- DTO, API, CLI, SDK, serialization, or OpenAPI contract drift
- async, retry, idempotency, cancellation, or observability gaps
- stale docs, generated artifacts, examples, or help text
- validation gaps or stale evidence

Anything found in this pass should be fixed before the worker pushes or hands off. If a category is not applicable, the
handoff can say so briefly rather than expanding the issue or pull request with boilerplate.

Prefer worker prompts that end at a handoff boundary. A worker subagent performs the assigned implementation or fix,
keeps its worktree consistent, runs required validation, commits and pushes when the prompt asks for it, and returns a
handoff with commit hash, changed files, validation evidence, blockers, and follow-ups. By default, workers should not
update GitHub issues, pull request bodies, review comments, conversation resolution, labels, checklists, merge state, or
cleanup records. The orchestrator owns those coordination updates. Once the handoff is sufficient, the orchestrator may
start the next independent worker before completing wrap-up for the prior worker, as long as the dependency graph and
write sets make that scheduling safe.

For high-risk slices, the orchestrator may assign a fresh pre-PR review worker before opening or updating the pull
request when that is cheaper than a likely bot/fix/re-review loop. Reserve this exception for changes that touch
authorization, storage/materialization, public contracts, generated clients or OpenAPI, async/idempotency behavior,
large cross-surface diffs, or slices where the implementation worker reports residual risk. This optional pre-PR review
does not replace Codex Code Review Bot as the blocking review gate.

After the first coding subagent that works on the issue commits and pushes the new branch to origin, the orchestrator
must open a normal ready-for-review pull request. The pull request can remain open while the step is still in progress.
From that point on, review findings, review fixes, validation evidence, and bot-review state belong on the pull request,
not only on the issue. Use the issue for claim, planning, parent/epic coordination, and pre-PR evidence; use the pull
request as the durable code-review ledger once it exists. Whenever the orchestrator adds or updates code-review comments
on a pull request, it must update the pull request body's `Review Status` section in the same turn. That section should
stay high-level: current head SHA, bot state for the current head, manual trigger decision, manual trigger lock text,
bot reviews observed, current open findings, fix commits already pushed, final no-issues bot status, and links to
detailed review/fix comments.

The implementation subagent must stop after committing, validating, and pushing the slice branch, then return a
[Ready For Review handoff](#ready-for-review-handoff) to the parent/orchestrator thread. The parent/orchestrator is
responsible for opening or updating the pull request and monitoring Codex Code Review Bot. Do not run `codex review`
through the shell, do not ask GitHub `@codex review`, and do not spawn local review-only subagents for the normal Grace
completion gate except for the selective high-risk pre-PR review described above or when the maintainer explicitly
changes the review mode for that task.

Codex Code Review Bot communicates through PR-body emoji reactions, review summaries, top-level PR comments, and
inline pull-request-review comments:

- 👀 on the pull request body means the bot has seen the latest pushed commit and is reviewing it.
- 👍🏻 on the pull request body means the bot found no issues for the latest reviewed commit.
- A pull request comment or inline pull-request-review comment from Codex Code Review Bot means it found one or more
  actionable issues.

The orchestrator must verify that the bot signal applies to the latest pushed commit before treating the review gate as
complete. Do not merge, close, or call a task review-complete while the latest bot state is still 👀, while a bot comment
is unresolved, or while the bot has not yet reacted to the current head commit.

### Manual Codex Review Trigger Lock

The manual Codex review trigger lock prevents duplicate review requests while Codex Code Review Bot is already working
on the current pull request head. If 👀 appears at any point for the current head commit, the only valid action is to
wait for either a 👍🏻 no-issues reaction or bot findings. Do not run `codex review`, ask GitHub `@codex review`, or use
any other manual trigger path while that lock is active.

The lock is active for the current head when any of these states exist:

- 👀 is present on the pull request body for the current head.
- 👍🏻 is present on the pull request body for the current head.
- Codex Code Review Bot has submitted a review for the current head.
- Codex Code Review Bot has written a top-level pull request comment or inline review comment for the current head.
- The available GitHub data is ambiguous, stale, incomplete, or cannot prove the bot state for the current head.

The missed-ack exception is the only allowed manual trigger path. It is available only after the minimum wait window has
elapsed since the current head commit or pull request update and the orchestrator has verified all of these conditions
for the current head:

- no 👀 reaction exists
- no 👍🏻 reaction exists
- no Codex Code Review Bot review exists
- no Codex Code Review Bot top-level comment exists
- no Codex Code Review Bot inline review comment exists

Use `pwsh ./scripts/guard-codex-review-trigger.ps1 -PullRequest <number>` before any missed-ack manual trigger decision.
The script is a one-shot guard, not a watcher. It prints a pasteable `Review Status` block and exits successfully only
when the missed-ack exception is allowed. A nonzero exit means the lock is active, the wait window has not elapsed, or
the state is ambiguous; in all of those cases, do not manually trigger review.

Do not rely on `gh pr view --json comments` alone when checking for bot findings. That field only covers top-level PR
comments and can miss inline comments stored under the pull request review. Inspect the latest bot review and its inline
comments before merging. A GraphQL check should include `reviews.nodes.comments.nodes`, for example:

```powershell
$query = @'
query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      headRefOid
      comments(first: 50) { nodes { author { login } body url } }
      reviews(first: 20) {
        nodes {
          author { login }
          state
          body
          url
          commit { oid }
          comments(first: 50) { nodes { path line body url } }
        }
      }
      reactionGroups { content users(first: 20) { totalCount nodes { login } } }
    }
  }
}
'@

gh api graphql `
  -f query="$query" `
  -F owner="<owner>" `
  -F name="<repo>" `
  -F number="<pr-number>"
```

The review loop is blocking:

1. After the implementation subagent pushes a commit, monitor the pull request for Codex Code Review Bot activity.
2. Wait for the bot to put 👀 on the pull request body for the current head commit, then wait for either a 👍🏻 reaction
   or a bot review comment.
   Use repeated checks no longer than 120 seconds each; do not run one long sleep, watch, `wait_agent`, or poll command
   while waiting for the bot.
   If 👀 appears at any point for the current head, the manual trigger lock is active and the only valid action is to
   wait for 👍🏻 or findings.
3. If the bot switches the PR-body reaction to 👍🏻 and there are no unresolved bot review comments for the current head,
   record that no-issues state in `Review Status` and continue toward merge readiness.
4. If the bot writes a top-level PR comment or inline pull-request-review comment with findings, update
   `Review Status`, then send the findings to a fresh implementation subagent to address in the issue-owned
   branch/worktree.
5. If the review finds a missing acceptance-criterion class, adversarial case, contract-propagation requirement, stale
   validation evidence, repeated trap, or issue-template gap that could affect active or future workers, amend the
   active and future sibling issues before spawning more workers. Update the issue template or agent docs when the trap
   is structural. Preserve issue history by appending an addendum unless replacing stale text is clearer and safe.
6. The implementation subagent re-runs focused validation for the changed behavior or docs, plus broader validation when
   the fix touches shared or risky surfaces.
7. The implementation subagent commits and pushes the review fix, then returns a new Ready For Review handoff.
8. The orchestrator replies to each Codex Code Review Bot top-level or inline review comment with the outcome, fix
   commit, and validation evidence using the [Review/Fix comment template](#reviewfix-comment-template). The comment
   must include a short prevention line and make the high-level outcome easy to scan before the detailed issue and fix
   text. The orchestrator resolves the GitHub conversation after the feedback has been satisfied.
9. The orchestrator updates the pull request body's `Review Status` section with the fix commit, validation evidence,
   and link to the bot comment or fix reply.
10. Wait for Codex Code Review Bot to review the new head commit. Repeat the loop until the bot reports no issues with
   a 👍🏻 reaction on the pull request body and all bot conversations are resolved.

Only after Codex Code Review Bot reports no issues for the latest commit can the task continue toward merge readiness,
handoff, or any other completion step. Record the final bot no-issues state and validation evidence in the pull request.

### Ready For Review Handoff

When the implementation agent is itself a subagent, it must not attempt to satisfy the review gate by running `codex`,
asking `@codex review`, spawning nested review work, or updating GitHub issues, pull requests, review comments, labels,
checklists, or merge/cleanup state unless the orchestrator explicitly delegated that GitHub update. Instead, it must
return this handoff to the parent/orchestrator thread:

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

### First-Pass Review Readiness

- Bot-prevention self-review: <completed; notable categories checked>
- Issues found and fixed before handoff: <none, or summary>
- Residual risks: <none, or summary>

### Orchestrator Follow-Up

- `<issue/PR/comment/checklist update needed, or none>`

### Review Request

Please monitor Codex Code Review Bot on the pull request. Do not run local review-only subagents, `codex review`, or
`@codex review` for the normal Grace completion gate. Manual trigger not attempted. The orchestrator must use the pull
request `Review Status` lock and `scripts/guard-codex-review-trigger.ps1` missed-ack guard before any manual trigger
path. Wait for the bot to acknowledge the latest commit with 👀, then wait for either a 👍🏻 no-issues reaction or a PR
comment with findings. If 👀 appears at any point, the only valid action is to wait for 👍🏻 or findings. If the bot
comments with findings, route the fix to a fresh implementation subagent, reply to the bot comment with the fix commit
and validation evidence, resolve the conversation, and wait for the next bot review on the new head commit.
```

If Codex Code Review Bot finds issues, the parent/orchestrator sends those findings to an implementation subagent. The
implementation subagent addresses the findings, validates, commits and pushes the fixes, and returns a new Ready For
Review handoff. The parent/orchestrator replies to the bot comment, resolves the conversation, updates `Review Status`,
and waits for the next bot review.

### Review/Fix Comment Template

Use this Markdown structure when replying to a Codex Code Review Bot comment after a review issue is fixed. Keep the top
section short and scannable; put detailed evidence below it.

```markdown
## Review/Fix: <short issue title>

**Status:** Fixed in `<commit-sha>`
**Review source:** Codex Code Review Bot
**Validation:** <command or check result>
**Prevention:** <root-cause class>; <current issue, sibling issues, template, or agent docs update needed?>

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

Use one of these root-cause classes in the `Prevention` line:

- acceptance-criteria / negative-proof gap
- contract-propagation gap
- CLI mode / side-effect interaction
- auth / materialization / traversal ordering gap
- algorithm adversarial-case gap
- validation / stale-evidence gap
- ordinary implementation mistake

Not every review finding requires a docs change. Ordinary implementation mistakes can be fixed and recorded without
changing templates or process docs. Repeated or structural traps should update active/future sibling issues before more
workers are assigned, and should update the issue template or agent docs when the missing guard belongs in future tasks.

## Validation Commands

Use the local scripts for repository validation:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
```

Use `-Fast` for the normal development loop. Use `-Full` when the change touches Aspire integration coverage, emulators,
storage, Cosmos DB, Service Bus, Redis, cross-service behavior, or deployment/runtime behavior.

Avoid duplicate builds. The validation ladder is:

1. Run Fantomas formatting or targeted Fantomas checks for touched F# files.
2. Run any required freshness or generated-file checks.
3. Choose exactly one final build/test gate.
4. Run `git diff --check`.

`validate.ps1 -Fast` and `validate.ps1 -Full` already restore, build the solution, and run the selected test projects.
If a worker is going to run `validate -Fast` or `validate -Full`, do not also prompt it to routinely run a
project-specific `dotnet build` plus `dotnet test --no-build`. The selected `validate` command is the final build/test
gate.

Focused project build/test is still appropriate when it is the right evidence for the slice:

- RED evidence before a code change.
- Failure diagnosis or faster defect localization after a failing broad gate.
- A skipped-validate workflow where the task record explicitly accepts the narrower gate.
- Tests outside the selected validate profile.
- Issues that explicitly require focused-only validation.

When a focused command uses `--no-build`, first run the matching
`dotnet build --configuration Release <project>` command so the test assembly exists and reflects the current source.
Use separate broad `dotnet build` or broad `dotnet test` commands only when diagnosing a failure or when `validate` is
being intentionally skipped.

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
3. Run focused project build/test only when it is needed for RED evidence, failure diagnosis, tests outside the selected
   validate profile, skipped-validate workflows, or explicitly focused-only issues.
4. Run exactly one final build/test gate, normally `validate -Fast` or `validate -Full`.
5. Run `git diff --check`.

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

Every pull request must link its related GitHub issue in the pull request body at creation time. For pull requests
targeting `main`, use one of GitHub's supported closing keywords when the merge should close the issue: `close`,
`closes`, `closed`, `fix`, `fixes`, `fixed`, `resolve`, `resolves`, or `resolved`. The standard Grace form is
`Closes #123` for same-repository issues. For pull requests targeting an epic integration branch, use non-closing
wording such as `Related to #123` or `Part of #249`; GitHub ignores closing keywords on non-default-branch PRs, so close
the sub-issue manually after the pull request merges to the epic branch.

When opening or updating a pull request, include the evidence available at that point and keep adding standalone
comments as the review loop continues:

- the linked GitHub issue
- why the change benefits Grace and its users
- summary of changed behavior
- touched paths and any write-set expansion
- focused validation run
- broader validation run, or skipped-validation reason
- implementation and review path used, including the implementation subagent and Codex Code Review Bot state observed
- final no-issues bot review result for the latest commit
- replies to each Codex Code Review Bot comment that required a fix, including the issue, fix, fix commit, validation,
  and resolved conversation state
- a `Review Status` section that summarizes the current review/fix state, current head SHA, bot state for the current
  head, manual trigger decision, manual trigger lock text, and links to detailed review/fix comments
- docs impact
- residual risk
- rollback or recovery notes when the change touches runtime or data
- useful AI prompts used for diagnosis or implementation, when contributing externally

Before the Grace completion review gate, update the branch against its required base:

- standalone non-epic issue branch: current `origin/main`
- sub-issue branch targeting an epic integration branch: current `origin/epic/<parent-issue>-<short-slug>`
- final epic-to-`main` branch: current `origin/main`

Then verify:

- ahead/behind status shows the branch is current enough for a blocking review decision
- the scoped diff still contains only the intended write set
- no unexpected deletions were introduced during the update
- the chosen validation gate has passed on the refreshed branch

Only then wait for Codex Code Review Bot to review the refreshed head commit. A bot reaction or comment on an older head
commit is useful history, but it does not satisfy the completion review gate.

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
6. Reply to the GitHub review comment with the outcome, changed commit, validation evidence, and a short prevention
   line.
7. Resolve the GitHub conversation after the feedback has been satisfied.

If the comment is ambiguous, conflicts with another requirement, or would cause a behavioral regression, ask for
clarification or reply with the trade-off instead of resolving the thread prematurely.

The prevention line must include one root-cause class and whether the current issue, sibling issues, issue template, or
agent docs need an update. Use the same classes from the [Review/Fix comment template](#reviewfix-comment-template).
Not every finding requires a docs change, but repeated or structural traps should update active/future issues before
more workers are assigned.

## Cleanup

After merge, promotion, or closing a pull request because the related issue/sub-issue work is complete:

1. Verify the destination branch or reference contains the change.
2. Confirm no uncommitted or unpushed work is stranded in the task workspace.
3. Delete the remote issue branch.
4. Remove task worktrees that are no longer needed and delete the local issue branch.
5. Run `git fetch --prune` and `git pull --ff-only` in the local repo so `main` is up to date.
6. Update the task record with final status and follow-ups.
7. Leave unrelated local changes untouched.

For epic integration branch mode, sub-issue cleanup retires the sub-issue branch and worktree after the sub-issue PR is
merged to the epic branch. Final epic cleanup also retires the epic branch and worktree after the epic-to-`main` PR is
merged and local `main` is fast-forwarded.

Do not wait for a separate user prompt before deleting remote branches. For agent-owned work, branch retirement is part
of closing the PR/issue lifecycle, not an optional follow-up.
