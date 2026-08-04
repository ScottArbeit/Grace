
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
- Run the smallest meaningful focused proof locally; use broader local validation only as an explicit escalation.
- Commit after each completed slice, then push one or more completed commits as a coherent reviewable checkpoint.
- Keep docs, README guidance, and nearby `AGENTS.md` files aligned with behavior and workflow changes.
- Include the purpose behind each feature or implementation change. Plans, issue bodies, and pull request bodies should
  explain why the work benefits Grace and its users so implementation agents can make better decisions when details are
  incomplete.
- Use the existing issue details to prevent likely current-head review findings before the pull request exists. This
  means sharper invariants, forbidden implementation shapes, adversarial cases, and test evidence rather than more
  issue-template ceremony.
- Grace is not in production. There is no production data to import, migrate, preserve, or grandfather. Do not weaken
  contracts, validators, generated clients, runtime behavior, or tests to preserve imaginary old data.

## No Production Data

Grace is still pre-production. There is no production data to import, migrate, preserve, or grandfather.

That premise has concrete review consequences:

- Public API, CLI, OpenAPI, SDK, and storage contracts should state the desired invariant for current Grace behavior.
- Do not broaden those contracts merely to accept missing, empty, or malformed values from hypothetical old data.
- Do not add import, migration, or grandfathering paths for data that does not exist.
- Review findings that depend on old production data start from the wrong premise. Correct the premise through
  documentation, source behavior, tests, or review response rather than weakening the public contract.
- Issue bodies and worker prompts should name any intentional compatibility exception explicitly. Silence means the
  current invariant wins.

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

For non-trivial epics, identify an early tracer-bullet vertical slice before broad parallelization. The tracer-bullet
slice should prove one narrow user-visible behavior through the closest stable public boundary, crossing the main
contract, runtime, persistence, validation, and documentation surfaces that the rest of the epic is likely to reuse. Use
what it reveals to refine child issues, owned paths, validation profiles, and parallelization boundaries before fanning
out into parallel implementation work.

Create the parent epic issue and child issues with `gh issue create --body-file`, using issue bodies written as separate
Markdown files in a temporary directory. For small issue sets, one small `Set-Content` or equivalent file-write command
per issue body is enough. For large issue batches, go directly to a short-lived generator script, either checked into the
worktree for the duration of the run or written under the temp directory. The script should read the plan or packet,
emit one Markdown body per issue, write a metadata file that maps conceptual issue keys to body paths, and stop before
any GitHub writes. Lint the generated Markdown files before creation.

Do not build one giant inline PowerShell command that embeds every Markdown body. Do not paste large generator scripts
through an interactive shell, and do not pass them as a single `pwsh -Command` string. Those paths waste time, flood the
transcript, and can hit Windows command-line length limits before the real work starts. Prefer file-backed generator
scripts and file-backed issue bodies so failed issue creation remains recoverable.

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
generated-artifact omissions, or validation gaps that a fresh reviewer is likely to flag if the worker misses them.
Prefer a few task-specific warnings over copying broad checklists. This is a quality bar for the existing issue fields,
not a new required section.

If review finds a missing class of acceptance criterion, adversarial case, contract propagation, or validation evidence,
update active and future sibling issues before assigning more workers. Preserve issue history by appending an addendum
unless replacing stale text is clearer and safe.

### Decision Closure And Contract Propagation

Before implementation begins on a product or architecture slice, close the decisions that would otherwise make workers
infer behavior from nearby code. Add the closure to the issue body, epic packet, or an issue addendum.

For each decision, record:

- decision name and status: `accepted`, `recommended default`, `deferred`, `waived`, or `open`
- recommended answer when the owner has not explicitly decided yet
- alternatives considered
- implementation impact
- proof impact
- the issue or sibling issue that owns deferred behavior

Use this gate for decisions about audience, authorization, visibility, ownership, billing, retention, migration, lifecycle
states, public event timing, failure behavior, default values, and whether an accepted input is implemented or rejected.

For each issue touching a public, durable, generated, or cross-project surface, add a compact contract propagation map.
Every row must be `updated`, `not changed`, `waived`, or `deferred to #issue`:

| Surface | Status | Notes / proof |
| ------- | ------ | ------------- |
| Shared DTOs, parameters, commands, events, persisted state | | |
| HTTP route, validation, authorization, error shape | | |
| CLI parser, JSON/stdout/stderr, help/examples | | |
| SDK or facade client | | |
| Static OpenAPI, generated clients, generator matrix | | |
| Events, webhooks, SignalR, watch, search, or projections | | |
| Docs, ADRs, agent guidance, runbooks | | |
| Focused tests and final validation | | |

Accepted inputs must be implemented, rejected, or explicitly classified as informational/non-triggering with proof. Do
not accept a field or flag that silently does nothing.

### Stale-Authority Preflight

For Watch, storage, materialization, authorization, eventing, runtime, or background-worker work, include a preflight that
names:

- authoritative state before decision
- identity tuple that distinguishes same, stale, duplicate, and conflicting work
- mutation or materialization window
- revalidation point immediately before write, publication, download, or cleanup
- terminal states and idempotent replay behavior
- cleanup/abort/quarantine behavior when a side effect succeeds but durable state rejects
- negative proof that stale snapshots, hidden resources, or failed cleanup cannot appear successful

This preflight should be task-specific. Prefer three precise stale-authority traps over a broad checklist copy.

### Risk-Surface Trap Checklist

Use this checklist to choose the task-local traps that belong in the minimum detail gate. Not every row applies to every
issue, but every selected or skipped row should be clear enough that a worker and reviewer can see what must be proven.

- Proof/test work: identify the false-positive test review is likely to catch. Prove the assertion would fail on
  regression, not just execute the path.
- DTO/contract work: check JSON shape, MessagePack or other serialization shape, OpenAPI component, aggregate OpenAPI,
  generated-client impact, SDK/facade impact, docs impact, and no-production-data posture. Remember that Grace has no
  production data to import or preserve; do not add compatibility paths for imaginary old data.
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
- Worker completed a review-prevention self-review over the actual diff before handoff
- A fresh Terra High review subagent following `dev-process/CODE_REVIEW.md` reported no blocking findings for the
  latest PR commit
- Required PR checks passed for that same latest commit
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

For implementation issues and pull requests, keep responsibility split between the main orchestrator and fresh
subagents:

- The orchestrator owns issue and PR coordination, review status, CI state, merge decisions, and cleanup.
- A fresh implementation or fix worker owns each code change, focused proof, commit, push, and handoff.
- The orchestrator does not implement or repair code as a substitute for a worker.
- A fresh review subagent owns the independent current-head code review and remains read-only.

After the first implementation worker pushes the issue branch, the orchestrator opens a normal ready-for-review pull
request. Keep the PR open while implementation, validation, review, and fixes continue so the current state remains
visible on the issue and PR.

### Execution Budget

Grace limits implementation and fix workers, not runtime proof:

| Start type | Default issue-level limit | Notes |
| ---------- | ------------------------- | ----- |
| Implementation or fix worker | 4 starts | Cumulative across replacements, compactions, branches, and model changes. |
| Aspire or other runtime start | Uncapped | Record the purpose and result; avoid overlapping runs unless the scenario requires concurrency. |
| Review subagent | One fresh session per PR head | Review sessions are tracked for stabilization, not charged to the worker limit. |

Record worker usage in the issue and PR `Review Status`. At the worker limit, or when a worker reports an owner gate,
preserve state and return to the owner before starting another implementation or fix worker. A runtime start does not
consume a worker start and does not require a larger numeric budget. Stop a runtime run when its proof is complete, and
do not create overlapping Aspire environments accidentally.

## Implementation Preflight

Before assigning the first implementation worker, the orchestrator must confirm that the issue is implementable from
its body alone. At minimum, record:

- the behavior invariant and the source that decides it;
- forbidden implementation shapes;
- positive, negative, regression, and boundary proof, or an explicit waiver for each;
- high-risk adversarial examples and selected risk-surface traps;
- owned, sensitive, and forbidden paths;
- the contract propagation map and any explicit N/A surfaces;
- stale-source revalidation, abort, retry, cleanup, and proof expectations when applicable;
- the focused validation profile and docs impact.

If product, domain, or architecture decisions remain open, stop implementation and complete specification or design
readiness first. Review is not the place to discover requirements that the issue should have stated.

### Worker Status And Self-Review

Every worker prompt must include the Grace status-file and heartbeat protocol from `AGENTS.md`. Before handoff, require
the worker to review the actual diff for likely current-head findings, correct issues it can see within scope, and
report:

- changed files and commits;
- focused validation commands and results;
- first-pass review readiness;
- residual risks and skipped validation;
- any issue or sibling-issue detail that should be strengthened to prevent recurrence.

### Current-Head Review Gate

For every PR revision that may be merged:

1. Read the installed `dev-process/CODE_REVIEW.md` instructions.
1. Start exactly one fresh review subagent with `model: gpt-5.6-terra`, `reasoning_effort: high`, and
   `fork_turns: none`.
1. Because the reviewer receives no forked conversation, provide the complete review context: repository and worktree,
   issue and PR links, base and head SHAs, intended behavior, owned and forbidden paths, validation evidence, known
   risks, and the exact diff scope.
1. Tell the reviewer to read `dev-process/CODE_REVIEW.md`, inspect the full current-head diff, remain read-only, and
   return its structured verdict and findings to the orchestrator. The reviewer must not edit files, push commits, or
   change GitHub state.
1. Run the fresh review concurrently with the required GitHub PR checks.
1. Wait for both the review verdict and the required checks. They satisfy the gate only when both apply to the same
   head SHA and both pass.

Any new commit makes the prior review verdict and prior check results stale. Start a new fresh reviewer and wait for
new required checks on the new head. Automatic review comments, reaction emoji, and manual review-trigger commands are
not Grace review state and are not part of this process.

### Finding Routing And Fix Serialization

After the reviewer completes, freeze the finding set for that head. Classify each finding as fix now, invalid, waived,
or deferred to a named future epic leaf. Record the reason and evidence for every non-fix disposition.

Route each fix-now finding to a fresh implementation or fix worker. Serialize fix workers for one PR unless the
completed review contains findings with provably disjoint write sets. A fix worker owns the code change, proof, commit,
push, and handoff. The orchestrator records the disposition and starts the new-head review/check pair.

For an epic-branch PR, a valid finding may be deferred only when it is explicitly outside the current leaf, a future
leaf owns it, and that future issue is updated with the finding and proof obligation. Never defer a finding that makes
a fact, persisted field, status flag, event, or trust predicate produced by the current leaf unreliable for later
leaves.

### Repeated Review Stabilization

Count a substantive cycle when a current-head reviewer reports a behavior, correctness, concurrency, recovery,
durability, contract, authorization, or maintainability finding; a worker fixes it; and the next current-head review
reports another substantive finding.

- After cycle 1, continue the normal fix loop.
- After cycle 2, add a repeated-theme prevention note to `Review Status`.
- After cycle 3, stop one-off patching and post a stabilization ledger to the issue and PR.
- After cycle 4, hard stop until the ledger is implemented, proven, and self-reviewed.

For high-risk surfaces such as storage, actors, retries, idempotency, authorization, public or persisted contracts,
concurrency, recovery, side-effect ordering, Watch state, or runtime timers, start stabilization after two substantive
cycles. If a PR exceeds three completed review-subagent sessions even without three substantive cycles, audit the
timeline before assigning another routine fix worker. Use `skills/code-review-stabilizer/SKILL.md` for the ledger and
status-map workflow.

### Ready For Review Handoff

An implementation or fix worker hands off when its current slice is committed, pushed, and supported by focused proof.
The orchestrator then owns the independent review/check gate.

Use this handoff shape:

```markdown
## Ready For Review

- Issue and PR:
- Base SHA:
- Head SHA:
- Commits:
- Changed paths:

### Validation

- Focused proof:
- Formatting or generated-artifact checks:
- Skipped validation and reason:

### First-Pass Review Readiness

- Worker self-review result:
- Residual risks:
- Suggested prevention updates:

### Orchestrator Follow-Up

- Start one fresh Terra High review subagent with `fork_turns: none`.
- Run it concurrently with required GitHub checks.
- Record the same-head verdict, checks, findings, and dispositions.
```

### Review/Fix Record Template

Record each finding and disposition on the PR:

```markdown
## Review/Fix: <short finding title>

- Reviewed head SHA:
- Review source: Fresh `gpt-5.6-terra` subagent using `dev-process/CODE_REVIEW.md`
- Finding classification: fix now | invalid | waived | deferred
- Finding or rationale:
- Fix commit, if any:
- Validation:
- Prevention update:
- New-head review/check status:
```

## Validation

Focused local proof establishes that a coherent commit or implementation slice is correct. GitHub `Validate` certifies
the current pull-request revision across the repository. Local Fast and Full are escalation, reproduction, and
diagnostic tools, not routine pre-commit or pre-push requirements.

| Stage | Default proof |
| ----- | ------------- |
| RED | Smallest test proving missing or incorrect behavior |
| GREEN/refactor | Same focused test or fixture |
| Before commit | Format/check, focused proof, freshness checks, `git diff --check` |
| Before push | Confirm local evidence is current; no routine broad local gate |
| PR current revision | GitHub `Validate`, authoritative broad gate |
| Review fix | Focused regression proof, then GitHub `Validate` |
| CI failure | Inspect CI evidence and reproduce narrowly; escalate to Fast or Full as needed |
| Merge | Current CI green, current review satisfied, residual risks recorded |

Use the local scripts only when their escalation role is justified:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
```

Fast is an optional broad local preflight for unavailable or delayed CI, unusually broad compile fan-out, an explicit
task or maintainer request, handoff without an immediate PR, or broader failure reproduction. Full is for local
Aspire-backed integration reproduction or diagnosis, emulator/storage/runtime investigation, unavailable CI integration
proof, an explicit request, or a defined release-candidate procedure. Do not infer a routine Full requirement merely
from touching an integration-related path.

Focused local proof is the normal path. For F# behavior changes, use RED where applicable, format touched files, build
the focused project before a `--no-build` test, run the smallest relevant fixture, namespace, category, or project, then
run freshness checks and `git diff --check`. Docs-only proof can be MarkdownLint, YAML or PowerShell parsing, rendered
HTML inspection, and `git diff --check`.

Some repetition is intentional: focused tests provide rapid local feedback and run again as independent CI
certification. Avoid routine local near-full or full validation immediately before the same broad repository proof in
CI. The validation ladder is:

1. Run Fantomas formatting or targeted Fantomas checks for touched F# files.
2. Run any required freshness or generated-file checks.
3. Choose exactly one final build/test gate.
4. Run `git diff --check`.

Fast keeps its selected non-Aspire filter. Full runs one unfiltered solution-level test command, including every current
and future test project in `src/Grace.slnx`. GitHub `Validate` restores and builds once, then invokes the shared Full
implementation without rebuilding. Do not add per-project test-process fan-out.

When Fast or Full is intentionally selected, do not also duplicate its build/test work with routine focused commands
for that checkpoint. Otherwise, local broad validation is normally omitted. That omission is not skipped validation
when focused proof is complete, the PR is pushed, and current GitHub `Validate` is required and available. Record
"skipped validation" only for omitted required focused proof, syntax/lint/freshness checks, required CI, or task-specific
manual validation.

Focused project build/test is still appropriate when it is the right evidence for the slice:

- RED evidence before a code change.
- Failure diagnosis or faster defect localization after a failing broad gate.
- Normal slice proof before current-revision GitHub `Validate`.
- Tests outside the selected validate profile.
- Issues that explicitly require focused-only validation.

When a focused command uses `--no-build`, first run the matching
`dotnet build --configuration Release <project>` command so the test assembly exists and reflects the current source.
Use separate broad `dotnet build` or broad `dotnet test` commands only when diagnosing a failure.

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

If required validation is skipped, record exactly what was skipped and why in the task record or pull request.

For F# changes, run Fantomas formatting or targeted Fantomas checks before build and test validation. The intended
order is:

1. Apply the code change.
2. Run Fantomas on the touched files, or run the repo-standard recursive Fantomas command when the edit is broad.
3. Run focused project build/test for the changed behavior.
4. Use Fast or Full only when their documented escalation condition applies.
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
- focused local proof, formatting/linting, freshness/syntax, and manual evidence as applicable
- optional Fast or Full evidence and the reason when one was run
- current head SHA, GitHub `Validate` conclusion, run link, and confirmation that it is associated with the latest PR
  revision; successful logs need not be summarized unless CI fails or warns
- implementation and review path used, including the implementation subagent and fresh review-subagent session
- final review-subagent verdict for the latest commit
- each finding disposition, including the finding, classification, fix commit when applicable, and validation
- a `Review Status` section that summarizes the current review/fix state, current head SHA, reviewer configuration,
  verdict, required check results, and links to detailed review/fix comments
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
- focused proof was rerun when conflict resolution or relevant base changes could affect the slice

Push the refreshed revision, run one fresh Terra High review subagent following `dev-process/CODE_REVIEW.md`
concurrently with the required GitHub checks, and wait for both. A review verdict or CI result on an older revision is
useful history, but it does not satisfy the completion review gate.

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
4. Add or strengthen focused regression proof, then run formatting, relevant syntax/freshness checks, and
   `git diff --check`. Use local Fast or Full only for an explicit escalation condition; GitHub `Validate` provides the
   new broad current-revision result.
5. Commit the fix and push the branch.
6. Reply to the GitHub review comment with the outcome, changed commit, validation evidence, and a short prevention
   line.
7. Resolve the GitHub conversation after the feedback has been satisfied.

If the comment is ambiguous, conflicts with another requirement, or would cause a behavioral regression, ask for
clarification or reply with the trade-off instead of resolving the thread prematurely.

The prevention line must include one root-cause class and whether the current issue, sibling issues, issue template, or
agent docs need an update. Use the same classes from the
[Review/Fix record template](#reviewfix-record-template).
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
