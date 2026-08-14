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

Keep each sub-issue small and clear enough that one implementation worker can execute it from the issue body without
inventing product semantics or the core state algorithm. One issue should deliver one user-visible outcome, own one
primary invariant family, and introduce at most one durable partial-state lifecycle.

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

### Factory Run Charter

- Outcome and supported world:
- Algorithm witness or N/A:
- Agent topology and review protocol:
- Owner stop conditions:
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

Grace uses a bounded, non-recursive factory run for each implementation issue or pull request. Freeze the run before
coding and do not change its process rules mid-run.

### Factory Run Charter

Record:

- issue, outcome, supported world, and quality contract;
- target branch and base SHA;
- algorithm-witness location and verdict, or justified N/A;
- validation profile;
- controller, implementation owner, discovery reviewer, and closure reviewer roles;
- review protocol and owner stop conditions.

A material process change stops the run, preserves current evidence, and starts a new charter. Do not reinterpret the
quality contract, expand issue scope, or change review rules while the feature is being repaired.

### Agent Topology

Use this topology:

- one short-lived controller for one issue or PR;
- one active issue-owner implementation worker;
- no nested subagents;
- one read-only R1 discovery reviewer;
- one read-only R2 closure reviewer when R1 produced accepted repairs.

The controller is the sole agent allowed to spawn subagents. Workers and reviewers must not spawn agents. Do not keep one
root controller alive across an entire epic.

The controller owns tracker state, branch and worktree coordination, review ledger, CI state, merge, and cleanup. It may
inspect code, diffs, logs, tests, and validation evidence and may run read-only or validation commands. It must not
silently replace the issue owner's implementation work.

The issue owner owns code, focused proof, self-review, commit, push, and all accepted in-scope repairs. Do not start a
fresh worker for each finding. Use one replacement only when the original worker is genuinely unavailable or its context
is unusable, and record the reason.

Do not require temp status files or fixed-interval heartbeats. Require a concise update before a long-running command,
when a material blocker or finding appears, and at handoff. Never sleep or poll for more than 120 seconds in one command.

Default to one active high-risk epic or factory-calibration stream. Parallelize Tier 2 production work only when the
owner explicitly approves independently proven outcomes, authority models, write sets, and integration paths.

### Execution Budget

| Role | Default run limit | Notes |
| --- | --- | --- |
| Issue-owner implementation worker | 1 | Owns implementation and accepted repairs. |
| Replacement implementation worker | 1 | Only when the original owner is unavailable or unusable; record why. |
| R1 discovery reviewer | 1 | One broad supported-world review of the coherent candidate. |
| R2 closure reviewer | 1 | Only when R1 produced accepted repairs; targeted to ledger and repair diff. |
| Runtime or Aspire starts | As needed | Record purpose and result; avoid accidental overlap. |

An algorithm witness is a separate bounded Discovery activity, not a way to add production workers. Children may never
spawn children.

### Implementation Preflight And Handoff

Before semantic edits, the issue owner confirms:

- supported outcome and non-goals;
- primary invariant, authority, identity, and commit point;
- algorithm-witness result;
- owned and sensitive paths;
- focused proof and validation profile;
- owner stop conditions.

Stop before coding when the issue contradicts current evidence, the witness, or the target branch.

Before handoff, the issue owner self-reviews the actual diff for:

- acceptance sequence and non-goals;
- quality contract and realistic supported producers;
- authority and effect ordering;
- public, persisted, generated, and documentation propagation;
- proof that could pass without establishing the claim;
- accidental or unowned changes.

Use this handoff:

```markdown
## Coherent Candidate Handoff

- Issue and PR:
- Base SHA:
- Candidate head SHA:
- Commits and changed paths:
- Outcome delivered and non-goals preserved:
- Focused proof and formatting/freshness checks:
- Manual or runtime evidence:
- Residual risk and skipped proof:
- Owner stop triggers: none | <trigger>
```

### R1 Discovery Review

Open one coherent ready-for-review PR after the first validated candidate. Do not use the PR as a scratchpad for
architecture discovery.

Start one independent R1 discovery reviewer in a fresh, read-only context. Supply the complete issue, Outcome
Charter, quality contract, supported world, algorithm-witness result, base and candidate head SHAs, exact diff,
proof, and non-goals. Require `dev-process/CODE_REVIEW.md` in **Discovery review** mode. The durable requirement
is independence from the implementation context, not a particular model name or client setting.

R1 performs one complete supported-world review and returns:

- PASS;
- PASS WITH ACCEPTED RISK;
- REPAIR with a finite Review Discovery Ledger;
- OWNER DECISION; or
- SUPERSEDE OR SPLIT.

Every actionable finding must name a supported producer, shortest supported sequence, contract basis, observable impact,
likelihood basis, required invariant, and closure proof. Reject unsupported-path hardening, stale or duplicate findings,
and product decisions presented as implementation defects.

If R1 passes and GitHub `Validate` passes on the same final head, R2 is not required.

### Frozen Review Discovery Ledger And Repair

When R1 returns findings, classify them once and freeze the accepted ledger for the run:

```markdown
## Review Discovery Ledger

- Candidate head SHA:
- Quality contract and supported world:
- R1 verdict:

| ID | Severity | Supported producer and sequence | Contract and impact | Required invariant and proof | Disposition | Status |
| --- | --- | --- | --- | --- | --- | --- |
| R1 | P1/P2/P3 | <producer> | <basis and impact> | <direction and proof> | fix/owner/risk/defer/reject | open |
```

Route all accepted in-scope findings to the same issue-owner worker for one consolidated repair pass when practical.
Repair commits do not reopen whole-surface discovery review.

For an epic-branch PR, defer a finding only when it is outside the current leaf and a named future issue owns the exact
behavior and proof. Do not defer a prerequisite that makes a fact, persisted field, status flag, event, or trust
predicate produced by the current leaf unreliable.

### R2 Closure Review

After accepted repairs are pushed, run one independent R2 reviewer on the repaired current head. Require
`dev-process/CODE_REVIEW.md` in **Closure review** mode.

R2 verifies only:

- each accepted ledger item;
- repair hunks and direct callers or consumers;
- direct regressions introduced by repair;
- current-head focused proof, generated or freshness checks, and required CI;
- scope and non-goal preservation.

R2 must not reopen untouched parts of the original diff for another unconstrained search. A straightforward incomplete
ledger repair may be corrected and rechecked in the same closure context.

There is no automatic R3. If R2 incidentally exposes a supported-world merge blocker outside the frozen ledger and
direct repair-regression scope, record one `DISCOVERY ESCAPE` with its shortest reproduction, contract basis, impact, and
location, then stop. Do not ignore it and do not keep searching. A new material invariant, authority boundary, state
machine, product semantic, or scope expansion is also an owner stop. Either case requires simplification, split,
supersession, or a new explicitly chartered run.

Use this closure record:

```markdown
## Closure Review

- Final head SHA:
- R2 verdict: VERIFIED | NOT VERIFIED | DISCOVERY ESCAPE | OWNER DECISION | SUPERSEDE OR SPLIT

| Ledger ID | Repair seam | Proof seam | Status | Residual risk |
| --- | --- | --- | --- | --- |
| R1 | <path/symbol> | <test/CI> | closed/open | <risk> |

- Direct repair regressions:
- Final GitHub `Validate`:
- Scope and non-goals preserved:
- Stop result:
```

### Process Stop Signals

Stop regardless of count when:

- the issue gains a second primary invariant, durable partial-state lifecycle, or authority boundary;
- a new domain concept or user-visible semantic is required;
- a third enabling PR is proposed before the tracer;
- the selected algorithm is no longer finite or proven;
- review is defining authority, lifecycle, ordering, recovery, or product behavior;
- process rules must change mid-run;
- the implementation contains more lifecycle or process machinery than user value.

Create a salvage map before superseding. Preserve independently correct decisions, tests, contracts, and code; reframe
useful behavior under a smaller supported world; defer optional automation; and identify code that must not be
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

Every pull request must link its related GitHub issue in the pull request body at creation time. For pull requests
targeting `main`, use one of GitHub's supported closing keywords when the merge should close the issue: `close`,
`closes`, `closed`, `fix`, `fixes`, `fixed`, `resolve`, `resolves`, or `resolved`. The standard Grace form is
`Closes #123` for same-repository issues. For pull requests targeting an epic integration branch, use non-closing
wording such as `Related to #123` or `Part of #249`; GitHub ignores closing keywords on non-default-branch PRs, so close
the sub-issue manually after the pull request merges to the epic branch.

When opening or updating a pull request, keep the evidence compact and current:

- linked GitHub issue and user-visible outcome;
- supported world, quality contract, and explicit non-goals;
- primary invariant and algorithm-witness result or N/A;
- touched paths and any owner-approved expansion;
- focused proof, formatting, freshness, generated-artifact, and manual evidence as applicable;
- optional Fast or Full evidence and the reason when one was run;
- final head SHA, GitHub `Validate` conclusion, and run link;
- R1 discovery verdict and frozen ledger;
- repair commits and ledger status when applicable;
- R2 closure verdict when repairs were required;
- docs impact, residual risk, skipped proof, and rollback or recovery notes.

Do not add one PR comment per internal worker action. Preserve the durable decision, finding disposition, proof, and final
closure state without turning the PR into an agent transcript.

Before the Grace completion review gate, update the branch against its required base:

- standalone non-epic issue branch: current `origin/main`
- sub-issue branch targeting an epic integration branch: current `origin/epic/<parent-issue>-<short-slug>`
- final epic-to-`main` branch: current `origin/main`

Then verify:

- ahead/behind status shows the branch is current enough for a blocking review decision
- the scoped diff still contains only the intended write set
- no unexpected deletions were introduced during the update
- focused proof was rerun when conflict resolution or relevant base changes could affect the slice

Push the coherent candidate, then run R1 discovery review under `dev-process/CODE_REVIEW.md` and required GitHub
checks. If R1 passes with no repairs and CI applies to that head, the completion gate is satisfied. If repairs are
accepted, freeze the R1 ledger, route one consolidated repair pass to the issue owner, then run R2 closure review and
required GitHub `Validate` on the final head. R1 remains the finite discovery record; R2 and final CI certify the repaired
revision. A repair commit does not authorize another whole-diff discovery review.

Open normal ready-for-review pull requests for Grace implementation work. Do not open draft pull requests unless the
maintainer explicitly asks for a draft.

Grace's product model uses promotion candidates, queues, gates, attestations, and review reports. Today's repository
still uses normal GitHub pull requests, but changes should be prepared so they are easy to audit in either system.

## Review Feedback

When asked to address a review comment or PR feedback:

1. Inspect the exact thread and classify it against the quality contract, supported producer, issue scope, and current
   R1 ledger.
2. Reject stale, duplicate, unsupported-path, or product-decision-as-defect feedback with evidence.
3. Route an accepted in-scope repair to the issue-owner worker. Do not create a fresh worker for each comment.
4. Add or strengthen focused regression proof, then run formatting, relevant freshness checks, and `git diff --check`.
5. Commit and push the repair.
6. Reply to the review thread with the outcome, commit, proof, and disposition, then resolve it when satisfied.
7. If this is an R1 ledger repair, include it in the consolidated repair pass and R2 closure review. Do not start a new
   broad discovery review merely because the head changed.

If the feedback requires a new product semantic, authority, state machine, recovery promise, or quality interpretation,
stop for an owner decision instead of patching locally.

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
