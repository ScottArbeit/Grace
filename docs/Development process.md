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
- Use a compact Outcome Charter and Issue Readiness gate before coding. State one outcome, the supported world, the
  Product V1 capability budget, the primary invariant and authority, explicit non-goals, the algorithm-witness result
  when required, focused proof, and owner stop conditions. Do not paste broad risk checklists into every issue.
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
an epic parent issue, but initially create only the earliest tracer and prerequisites proven necessary by current
evidence. The parent owns the outcome, current dependency map, capability dispositions, and integration status. Each
created child owns one implementation slice, branch/worktree, validation path, bounded review, and pull request. Re-plan
and create later children after the tracer runs. Assign every created child's parent relationship to the epic in GitHub
Relationships.

An epic is a durable product and planning record, not a durable agent conversation. Use a fresh epic-checkpoint session
to select the first child, after each merged child to update the DAG and select at most one next Tier 2 child, and
to close the epic. End each issue controller after its issue or pull request is merged, stopped, or superseded.

Use one agent for an epic checkpoint by default. It may spawn at most two read-only scouts with explicit
`fork_turns = "none"` when separate codebase, tracker, or architecture questions are concrete and independent. The scouts
must not edit source or tracker state, and the checkpoint agent must synthesize one durable checkpoint before activating
new work. Do not spawn implementation workers from an epic checkpoint.

When planning a feature or epic, state why the change matters before decomposing the work. Connect the work to the
benefit for Grace and its users: improved trust, safer operations, clearer contracts, faster workflows, lower operator
risk, better product fit, or another task-specific outcome. Carry that purpose into the parent epic, child issues, and
pull request bodies. This context lets implementation agents choose better local tradeoffs when the plan leaves a gap
or an acceptance criterion is ambiguous.

For non-trivial epics, identify the earliest value-bearing tracer before broad implementation. It should prove one
narrow user-visible outcome through the closest stable boundary and cross only the contract, runtime, persistence, and
proof seams required for that outcome. Allow at most two production pull requests before the tracer, and prefer zero or
one. A third prerequisite is a stop signal to simplify the module boundary, supported world, or tracer. Re-plan later
issues from the running tracer rather than pre-creating a large horizontal issue forest.

Create the parent epic and the small currently approved child set with `gh issue create --body-file`, using separate
Markdown body files in a temporary directory. A generator script is appropriate only when the owner explicitly approves
a larger ready set after tracer evidence. It should emit one Markdown body per approved issue, write a metadata map,
and stop before GitHub writes. Lint generated Markdown before creation.

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

The parent issue must include a compact dependency map for the currently created issues that shows:

- the tracer and every proven prerequisite as a node
- dependencies between created issues
- any owner-approved parallelism
- the expected integration order

Future capabilities may remain a high-level backlog or capability list. Do not create implementation-ready nodes merely
to make the DAG look complete.

Treat that product DAG as necessary but not sufficient for parallel execution. A step can be behaviorally independent
while still creating predictable merge churn. Before assigning parallel branches, compare the expected write sets:

- Parallelize only when branches are disjoint enough to avoid routine conflict resolution.
- Serialize or merge-queue branches that touch shared project files such as `*.fsproj`, `Startup.Server.fs`, or the
  same test/helper files.
- For broad waves, consider a preparatory compile-item or file-scaffold slice, then let later branches edit separate
  files.

### Epic Delivery And Merge Strategy

Keep epics for product planning and traceability, but choose the branch strategy independently for each ready child.
Record one of these modes in the parent issue and child Run Charter.

#### Mainline slice mode, default

Create `agent/<issue-number>-<short-slug>` from current `origin/main`, target the pull request to `main`, and use a closing
keyword when merge should close the issue.

Use mainline slice mode when the child:

- is independently correct under its issue contract;
- is safe or inert until later capability consumes it;
- does not expose a half-active public or durable semantic;
- does not depend on an unmerged sibling to keep produced facts truthful; and
- can pass required CI against current `main`.

Membership in an epic does not require an epic branch.

#### Epic integration branch mode, exception

Use `epic/<parent-issue>-<short-slug>` only when the owner explicitly accepts that:

- a child cannot safely exist in `main` before sibling work, or composition must be proven before release;
- CI validates pull requests targeting `epic/**`, or an equivalent recorded integration gate exists;
- the branch has a refresh and baseline-admissibility policy;
- every child PR is evaluated both against the epic branch and as part of the eventual delivery delta to `main`; and
- one final epic-to-`main` PR is the release candidate.

Do not select integration-branch mode merely to mirror the issue hierarchy or preserve old commit ancestry.

When using an epic integration branch:

- Keep the parent issue DAG, checklist, and merge strategy clear about which sub-issues target the epic branch.
- Keep the epic branch admissible and refreshed from `origin/main` before later child waves and before the final PR.
- Prove restore, build, and required integration behavior after any non-trivial refresh before child semantic work starts.
- Treat project, solution, package, generated-surface, runtime-topology, language-migration, persistence, authorization,
  and public-contract conflicts as owner decisions rather than mechanical conflict resolution.
- Treat each child as complete when reviewed, validated, merged to the epic branch, and cleaned up.
- Treat the epic as complete only after the final epic-to-`main` PR is reviewed, validated, merged, and cleaned up.
- Use non-closing issue wording for child PRs and close the child manually after merge when appropriate.

The parent issue must include a sub-issue checklist and a compact dependency map for the currently created children. As
children complete, update the checklist and use a fresh epic checkpoint to select the next child.

Keep each child small enough that one implementation owner can execute it from the issue body without inventing product
semantics or the core state algorithm. One issue should deliver one value-bearing outcome, own one primary invariant
family, and introduce at most one durable partial-state lifecycle.

### Outcome Charter And Capability Budget

Before issue decomposition, record a compact Outcome Charter:

- user-visible outcome;
- supported actor, client, environment, topology, and producer paths;
- required, deferred, rejected, and out-of-scope capabilities;
- primary invariant family and authoritative source;
- stable user-observable proof seam;
- Product V1 capability budget and owner stop conditions.

For Product V1, default to one supported environment, one primary authority at each decision point, no more than one new
durable partial-state lifecycle, and no background scheduler, automatic reconciliation, credential rotation, or
generalized recovery unless the named outcome requires it. Remove optional capability before coding rather than
retaining it and weakening correctness in review.

### Algorithm Readiness Gate

A Plan-ready specification is not algorithm-ready when correctness still depends on unresolved effect order, commit
points, filesystem residue, restart, replay, cleanup, concurrency ownership, or interaction between authorities.

Before production coding, require a disposable executable algorithm witness for:

- destructive or externally irreversible mutation;
- durable partial progress;
- filesystem atomicity or multi-store ordering;
- retries with ambiguous outcomes;
- crash, restart, replay, or cleanup semantics;
- concurrent coalescing, deduplication, or ownership transfer;
- background timers or schedulers;
- more than one authoritative store or service.

The witness must expose states and effects, inject failure at meaningful boundaries, discard in-memory state and restart
from captured durable or physical residue, and produce one verdict: proven, simplified, or blocked. Keep it outside the
production branch. Propagate the selected state, effect order, commit point, residue, retry, and non-goal decisions into
the specification and issue.

Do not add issue prose to compensate for an algorithm that has not been demonstrated.

### Compact Issue Readiness

Before assigning or claiming an implementation issue, require:

```markdown
## Outcome

<One supported user-visible result.>

## Supported world

- Quality contract and overrides:
- Actor or client:
- Environment and topology:
- Producer paths:

## Scope and explicit non-goals

- Required now:
- Deferred, rejected, or out of scope:

## Primary invariant and authority

- Invariant:
- Authoritative source:
- Identity and commit point, when relevant:

## Algorithm readiness

- Witness and verdict, or justified N/A:
- Effect-order and residue decisions propagated:

## Acceptance sequence

1. <supported action>
2. <system behavior>
3. <observable result>
4. <retry, restart, or failure behavior only when included>

## Required failure behavior

| Supported producer or failure | Expected result | Proof |
| --- | --- | --- |
| <trigger> | <result> | <test or fixture> |

## Contract and persistence propagation

- Updated surfaces:
- Unchanged or N/A surfaces with reason:

## Paths

- Owned:
- Sensitive or forbidden:

## Proof and validation

- Focused failing proof:
- Positive, negative, and boundary proof:
- Failure-injection or restart proof, when included:
- Generated or freshness checks:
- Required GitHub `Validate` expectation:

## Stop conditions

Stop before further coding if a second primary invariant, durable state machine, authority boundary, product semantic,
material topology expansion, or third pre-tracer enabling PR becomes necessary.

## Definition of done

- The acceptance sequence works through the stable boundary.
- Required focused proof and final-head CI pass.
- Explicit non-goals remain absent.
- R1 discovery review is complete.
- Accepted ledger items, if any, are repaired and R2 closure-reviewed.
- Residual risk and skipped proof are recorded.
```

Apply only risk prompts relevant to the selected outcome and supported world. Do not require exhaustive N/A inventories,
speculative adversarial examples, or a checklist for every possible Grace surface.

An accepted input must be implemented, rejected, or explicitly informational. A deferred capability must not leave a
half-active flag, timer, route, persisted state, or public contract.

When public, durable, generated, or cross-project behavior changes, use a compact propagation map for surfaces the slice
actually touches:

| Surface | Updated, unchanged, deferred, or N/A | Proof or reason |
| --- | --- | --- |
| Shared DTOs, parameters, commands, events, or persisted state | | |
| HTTP route, validation, authorization, and error shape | | |
| CLI, SDK, OpenAPI, and generated clients | | |
| Events, watch, search, projections, or other consumers | | |
| Docs, examples, tests, and validation | | |

If review discovers a missing product decision, algorithm, authority, or primary invariant, stop the run and update the
canonical specification and future issue packet. Do not append more workers to the current run.

## Workspace

After the GitHub issue exists, claim it before editing, assign it to the authenticated GitHub user, and create an
issue-owned branch and worktree from the selected admissible base:

- mainline slice, including a child of an epic: current `origin/main`;
- integration-branch child: current admissible `origin/epic/<parent-issue>-<short-slug>`; or
- final epic release candidate: current epic branch after refreshing and proving it against current `origin/main`.

Before any semantic edit, complete the Baseline Admissibility Packet from `dev-process/TEMPLATES.md`. Record the exact
base SHA, eventual delivery target, restore/build proof, semantic conflict classification, issue diff, delivery delta
against `main`, and any source-level salvage. A focused project test is not sufficient to certify a newly merged solution
or runtime topology.

Prior branches and commits are salvage sources rather than ancestry requirements unless the issue names an explicit
compatibility reason. Transplant selected code, tests, or decisions when that is cheaper and safer than merging stale
lineage.

Post a claim comment and assign the issue to the authenticated GitHub user before editing:

```markdown
## Claimed

**Execution mode:** direct single-agent | controller/worker

**Root issue session:** <session or run id>

**Implementation owner:** root | <worker thread name, after spawn>

**Branch:** `agent/<issue-number>-<slug>`

**Delivery mode:** mainline slice | epic integration branch

**Base:** `<branch>@<exact SHA>`

**Delivery target:** `<branch>`

### Baseline Admissibility

- Verdict and evidence link:
- Delivery-delta comparison:
- Prior lineage is salvage only, unless otherwise stated:

### Planned Write Set

- <path 1>
- <path 2>

### Forbidden Paths Acknowledged

- <path A>
- <path B>

### Validation Planned

- <focused tests>
- <baseline and final broad proof>

### Validation Profile

_<profile from docs/Development process.md>_

### Factory Run Charter

- Outcome and supported world:
- Algorithm witness or N/A:
- Agent topology and context-fork policy:
- Owner stop conditions:
```

Recommended branch name:

```text
agent/<issue-number>-<short-slug>
```

Mainline worktree shape:

```powershell
git fetch --prune origin
git worktree add ../Grace-gh-184 -b agent/184-short-slug origin/main
Set-Location ../Grace-gh-184
git status --short --branch
```

Optional epic integration branch shape, only after owner approval:

```powershell
git fetch --prune origin
git worktree add ../Grace-epic-184 -b epic/184-short-slug origin/main
git push -u origin epic/184-short-slug
git worktree add ../Grace-gh-185 -b agent/185-short-slug origin/epic/184-short-slug
Set-Location ../Grace-gh-185
git status --short --branch
```

When a task assigns a worktree different from the thread workspace root, every `apply_patch` filename must be an absolute
path under the assigned worktree. After the first patch, verify `git status --short --branch` in both the assigned
worktree and the workspace root.

If unrelated changes already exist, leave them alone. If they affect the task, work with them instead of reverting them.
If the task must expand beyond the issue's owned paths, stop before editing and use an Owner Decision Packet. Do not
silently broaden the write set.

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

## Agent Execution And Review

Grace uses a bounded, non-recursive factory run for each implementation issue or pull request. Subagents are optional,
not automatic. Freeze the execution mode and review rules before coding and do not change them mid-run.

### Work Hierarchy

Use four distinct levels:

1. **Epic:** durable product outcome, accepted decisions, current small DAG, and integration status.
2. **Epic checkpoint session:** fresh read-only planning session before the first child, after each merged child, and at
   epic closure.
3. **Issue execution session:** one root session from issue activation through merge, owner stop, or supersession.
4. **Optional child agent thread:** one bounded worker, scout, or reviewer role inside the issue execution session.

Do not use a child thread as an issue, an issue session as an epic, or hidden conversation state as a durable handoff.

### Choose The Issue Execution Mode

Record one mode in the Run Charter:

- **Direct single-agent mode:** default for Tier 0 and bounded Tier 1 work with a stable contract, one worktree, and no
  expected algorithm, topology, authority, or delivery-mode decision. The root session implements, validates, commits,
  coordinates the pull request, and may spawn one read-only R1 reviewer. It does not spawn an implementation child.
- **Controller/worker mode:** required for Tier 2, factory calibration, stateful or destructive behavior, complicated
  integration, or work expected to span implementation, CI, and repair turns. The root coordinates one issue-owner
  worker and does not write code as a substitute for that worker.

Use one implementation owner in either mode. Never parallelize writers on one issue. Choosing direct mode does not waive
independent R1 review or final-head CI.

### Factory Run Charter

Record:

- issue, parent epic, outcome, supported world, and quality contract;
- execution mode: direct single-agent or controller/worker;
- delivery mode, target branch, exact base SHA, and eventual delivery target;
- Baseline Admissibility verdict;
- algorithm-witness location and verdict, or justified N/A;
- validation profile, owned paths, and forbidden paths;
- controller, implementation owner, optional diagnostic scout, discovery reviewer, and closure reviewer roles;
- context-fork policy, review protocol, execution budget, and owner stop conditions.

A material process, base, delivery-mode, or scope change stops the run, preserves current evidence, and starts a new
charter. Do not reinterpret the quality contract or change review rules while the feature is being repaired.

### Baseline Admissibility

Before the implementation owner edits semantic code:

1. pin the base and eventual delivery target to exact SHAs;
2. run the required restore, build, and integration checks on the base, or record a proven target-branch failure;
3. inspect both the issue delta and the eventual delivery delta against `main`;
4. classify every refresh conflict;
5. stop before resolving conflicts that choose project shape, solution membership, package policy, generated output,
   runtime topology, language migration, persistence, authorization, or public contracts; and
6. distinguish source-level salvage from ancestry requirements.

After a stale-lineage merge or rebase, broad base proof comes before semantic child work. A focused component build does
not make a hybrid solution or runtime topology admissible.

### Agent Topology And Context

The root issue session is the only agent allowed to spawn subagents. Workers, scouts, and reviewers must not spawn agents.
Add project-scoped custom agent configurations under `.codex/agents/` and set child roles to read-only or
workspace-write as appropriate with multi-agent tools disabled.

The bounded child roles are:

- no implementation child in direct single-agent mode;
- one issue-owner implementation worker in controller/worker mode;
- zero diagnostic scouts by default, with at most one read-only scout active when root cause is unknown;
- one read-only R1 discovery reviewer; and
- one R2 closure review only when R1 produced accepted repairs, preferably by resuming the R1 reviewer thread.

For tracked implementation and review, always specify `fork_turns`; do not omit it because the current Codex default is
full history. Every new child uses `fork_turns = "none"` and receives a complete Subagent Run Packet. A read-only scout
may receive the smallest positive turn count, normally `"1"` or `"2"`, only when the immediately preceding failure
exchange is itself evidence and the reason is recorded. Do not use `fork_turns = "all"` for tracked implementation or
review.

Before work, each child acknowledges its role, exact SHA, workspace, delivery target, supported world, owned or review
paths, non-goals, stop conditions, and proof or review mode. Interrupt it before edits if the acknowledgement is wrong.

The root owns tracker state, branch and worktree coordination, CI, review ledger, owner decisions, merge, and cleanup. In
direct mode, it also owns code, focused proof, self-review, commit, push, and accepted in-scope repairs. In
controller/worker mode, those implementation duties belong to the one issue-owner worker, and the root must not silently
replace it.

One implementation owner means one continuing identity, not one turn. In direct mode, continue the root session for
compiler corrections, focused test failures, owned-path CI failures, and one consolidated accepted R1 repair ledger. In
controller/worker mode, resume the same worker with `followup_task` or the client equivalent for those tasks.

### Worker Continuation And Owner Decisions

Use this decision table at every material blocker:

| Classification | Condition | Next action |
| --- | --- | --- |
| Continue implementation owner | Local in-scope defect; contract, base, topology, and algorithm remain valid. | Continue the root in direct mode or resume the existing worker in controller/worker mode. |
| Diagnostic scout | Root cause is unknown and read-only evidence can reduce uncertainty. | Spawn one read-only scout, then return evidence to the same implementation owner. |
| Replacement worker | In controller/worker mode, the original worker is unavailable, lost, tool-broken, or remains scope-incoherent after one correction. | Spawn one fresh replacement with no inherited turns and the complete packet. |
| Owner stop | Product, scope, base, delivery mode, project shape, topology, authority, state, public contract, or quality must change. | Preserve state and issue one Decision Packet. |
| Supersede or split | Issue boundary, algorithm, or architecture is invalid, or the same invariant survives closure. | Produce a salvage map and smaller charter. |

Do not ask “May I try one more worker?” A replacement is not permission to repeat the same approach. One replacement is
the maximum in a run, and the reason must be durable. If it reaches the same architecture blocker, stop.

An Owner Decision Packet must name the one decision, evidence, classification, no more than three viable options,
recommendation, exact tracker and repository effects, salvage disposition, and exact continuation command or prompt.

Do not require temp status files or fixed-interval heartbeats. Require concise updates before a long-running command,
when a material blocker or finding appears, and at handoff. Never sleep or poll for more than 120 seconds in one command.

Default to one active high-risk epic or factory-calibration stream. Parallelize Tier 2 production work only when the owner
explicitly approves independent outcomes, authority models, write sets, delivery targets, and integration proof.

### Execution Budget

| Role | Default run limit | Notes |
| --- | --- | --- |
| Direct-mode implementation root | 1 when selected | No implementation child; continues through one consolidated repair pass. |
| Controller/worker implementation identity | 1 when selected | May receive multiple directed turns and owns one consolidated repair pass. |
| Diagnostic scout | 0, maximum 1 active | Read-only evidence only; never becomes an implementation owner. |
| Replacement worker | 0, maximum 1 | Controller/worker mode only; use when the original thread is unavailable or unusable and record why. |
| R1 discovery reviewer | 1 | One broad supported-world review of the coherent candidate. |
| R2 closure reviewer | 1 when needed | Resume R1 when available; otherwise one fresh read-only reviewer. |
| Runtime or Aspire starts | As needed | Record purpose and result; avoid accidental overlap. |

An algorithm witness is a separate bounded Discovery activity, not a way to add production workers.

### Implementation Preflight And Handoff

In direct single-agent mode, the root verifies the frozen Run Charter, exact base, owned paths, non-goals, stop
conditions, and proof plan before editing. No implementation Subagent Run Packet is needed.

In controller/worker mode, the controller sends the complete Subagent Run Packet before the worker edits. The worker
acknowledges it and proceeds only when it matches the frozen run.

At coherent-candidate handoff, the implementation owner reports:

- base and candidate SHAs;
- changed paths and eventual delivery delta;
- acceptance and non-goal status;
- focused proof and commands;
- required validation not run;
- self-review results;
- residual risk and skipped proof; and
- availability for the consolidated repair pass.

### R1 Discovery Review

Start R1 only after the coherent candidate has current focused proof and any required baseline-level or final-head CI gate
selected by the Run Charter. For a rebaseline or newly composed integration branch, require the broad admissibility gate
before spending R1.

Spawn one fresh read-only reviewer with `fork_turns = "none"` and a complete Review Run Packet. R1 inspects the pinned
three-dot issue diff, relevant callers and proof, and the eventual delivery delta against `main` when applicable. It
produces one finite ledger and stops searching after the supported world has been reviewed once.

### Frozen Review Discovery Ledger And Repair

Classify R1 findings once. Reject unsupported, duplicate, stale, speculative, or product-decision findings with evidence.
Freeze accepted findings, then continue the direct-mode root or resume the controller/worker-mode issue owner for one
consolidated repair pass. Repair commits do not reopen whole-surface discovery review.

### R2 Closure Review

When accepted repairs were pushed, resume the R1 reviewer for targeted R2 when available. If that thread is unavailable,
spawn one fresh read-only reviewer with `fork_turns = "none"`, the frozen ledger, repair diff, and complete closure packet.

R2 verifies only accepted ledger items, repair hunks and direct effects, direct repair regressions, current-head proof and
CI, and scope, delivery-mode, delivery-delta, and non-goal preservation. It must not reopen untouched code.

There is no automatic R3. A straightforward incomplete ledger repair may be corrected by the same implementation owner and rechecked in
the same closure context. A `DISCOVERY ESCAPE`, new material invariant, authority boundary, state machine, public semantic,
or scope expansion stops the run for a new charter, split, or supersession.

### Process Stop Signals

Stop regardless of count when:

- the base or refreshed integration result is not admissible;
- a semantic merge or rebase conflict requires an architectural choice;
- the eventual delivery delta against `main` contains deferred capability not visible in the issue diff;
- preserving ancestry imports capability outside the current budget;
- the issue gains a second primary invariant, durable partial-state lifecycle, authority boundary, or product semantic;
- a third enabling PR is proposed before the tracer;
- the selected algorithm is no longer finite or proven;
- review is defining authority, lifecycle, ordering, recovery, or product behavior;
- delivery mode or process rules must change mid-run; or
- implementation contains more lifecycle or process machinery than user value.

Create a salvage map before superseding. Preserve independently correct decisions, tests, contracts, and code; reframe
useful behavior under a smaller supported world; defer optional automation; and identify code that must not be merged or
cherry-picked wholesale.

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
| Coherent candidate before R1 | Focused proof, freshness checks, `git diff --check`, and pushed head |
| Candidate needing no repair | R1 Discovery verdict plus GitHub `Validate` on the same final head |
| Accepted R1 repair | Focused regression proof for the frozen ledger and repair diff |
| Closure after repair | R2 Closure verdict plus GitHub `Validate` on the repaired final head |
| CI failure | Inspect newest current-head failed workflow logs; classify every failure, reproduce only grounded in-scope failures narrowly, and escalate to Fast or Full as needed |
| Merge | R1 ledger closed, R2 complete when repair occurred, final CI green, and residual risks recorded |

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

Every pull request must link its related GitHub issue in the pull request body at creation time.

- A mainline-slice PR targets `main` and normally uses `Closes #123` when merge should close the issue.
- An integration-branch child PR uses non-closing wording such as `Related to #123` or `Part of #597`; close the child
  manually after merge when the work is complete.
- A final epic release PR targets `main` and links the parent epic and included children.

Keep pull-request evidence compact and current:

- linked issue and outcome;
- delivery mode, exact base, eventual delivery target, and Baseline Admissibility verdict;
- supported world, quality contract, and non-goals;
- primary invariant and algorithm-witness result or N/A;
- issue diff and eventual delivery delta against `main`;
- touched paths and owner-approved expansion;
- focused proof, formatting, freshness, generated checks, and runtime evidence;
- final head SHA and GitHub `Validate` result;
- R1 verdict and finite ledger;
- repair commits and R2 result when required; and
- residual risk, skipped proof, and rollback or recovery notes.

Do not add one PR comment per internal worker action. Preserve durable decisions, finding dispositions, proof, and closure
without turning the PR into an agent transcript.

Before completion review, update against the declared delivery-mode base:

- mainline slice: current `origin/main`;
- integration-branch child: current admissible `origin/epic/<parent-issue>-<short-slug>`;
- final epic release: current `origin/main`.

Then verify:

- exact base and ahead/behind status;
- Baseline Admissibility still holds after the update;
- the issue delta contains only intended work;
- the eventual delivery delta against `main` contains no deferred or half-active capability;
- no unexpected deletions or topology changes were introduced; and
- focused and broad proof were rerun when conflict resolution or base changes could affect the slice.

Project, solution, package, generated-surface, runtime-topology, language-migration, persistence, authorization, or public-
contract conflicts are owner stops unless the issue already selected the exact resolution. Do not publish a semantic
integration merge first and ask the owner afterward.

Push one coherent candidate. Run R1 once under `dev-process/CODE_REVIEW.md`. If R1 passes with no repairs and CI applies
to that head, the completion gate is satisfied. If repairs are accepted, freeze the ledger, continue the direct root or
resume the controller/worker-mode issue owner for one consolidated repair pass, then run R2 closure and final-head CI. A repair commit does not authorize another whole-diff
review.

Open normal ready-for-review pull requests. Do not open draft pull requests unless the maintainer explicitly asks for one.

Grace's product model uses promotion candidates, queues, gates, attestations, and review reports. Today's repository uses
normal GitHub pull requests, but changes should remain easy to audit in either system.

## Review Feedback

When asked to address a review comment or PR feedback:

1. Inspect the exact thread and classify it against the quality contract, supported producer, issue scope, and current
   R1 ledger.
2. Reject stale, duplicate, unsupported-path, or product-decision-as-defect feedback with evidence.
3. Route an accepted in-scope repair to the same implementation owner. Do not create a fresh worker for each comment.
4. Add or strengthen focused regression proof, then run formatting, relevant freshness checks, and `git diff --check`.
5. Commit and push the repair.
6. Reply to the review thread with the outcome, commit, proof, and disposition, then resolve it when satisfied.
7. If this is an R1 ledger repair, include it in the consolidated repair pass and R2 closure review. Do not start a new
   broad discovery review merely because the head changed.

If the feedback requires a new product semantic, authority, state machine, recovery promise, or quality interpretation,
stop for an owner decision instead of patching locally.

## Cleanup

After merge, promotion, or intentional closure:

1. verify the destination contains the change;
2. confirm no uncommitted or unpushed work is stranded;
3. delete the remote issue branch;
4. remove the task worktree and delete the local issue branch;
5. run `git fetch --prune` and update local `main` with `git pull --ff-only`;
6. update the issue and parent epic with final status and real follow-ups; and
7. leave unrelated local changes untouched.

For mainline-slice mode, the issue controller ends after merge and cleanup. Start a fresh epic-checkpoint session to read
current `main`, the merged PR, the parent epic, and the canonical specification, then select at most one next Tier 2 child.

For integration-branch mode, child cleanup retires the child branch and worktree after merge to the epic branch. Prove the
epic branch remains admissible before activating the next child. Final epic cleanup retires the epic branch and worktree
after the epic-to-`main` PR merges.

After supersession, preserve branches and worktrees only until the salvage comparison and replacement PR make them
unnecessary. Then retire them explicitly. Do not keep invalid integration branches as accidental future bases.

Do not wait for a separate user prompt before deleting agent-owned remote branches once preservation conditions are
satisfied. Branch retirement is part of closing the PR and issue lifecycle.
