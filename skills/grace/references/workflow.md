# Grace Workflow

This reference is a Grace-specific router. It does not duplicate the portable development process or the complete
repository workflow.

## Current Sources Of Truth

Use, in order:

1. current owner request and decisions;
2. root and nearest `AGENTS.md`;
3. `docs/Development process.md`;
4. installed `dev-process` skill, including `QUALITY-CONTRACTS.md`, `CODE_REVIEW.md`, and `TEMPLATES.md`;
5. the linked issue, pull request, and canonical specification;
6. current source, tests, contracts, generated artifacts, CI, and GitHub state.

`dev-process` owns quality contracts, capability budgets, algorithm readiness, tracer selection, agent topology, bounded
R1/R2 review, review recovery, and owner stop conditions. This file records only Grace repository mechanics and useful
routing reminders.

## Planning Versus Tracked Work

- Planning-only requests stay in chat or the canonical specification.
- Create GitHub issues, branches, worktrees, or pull requests only when the user requests tracked work or implementation.
- Use GitHub issues and pull requests as the durable implementation coordination surfaces.
- Do not create a parallel durable task ledger.
- For multi-step work, use an epic plus linked child issues only after `dev-process` has selected the earliest
  value-bearing tracer and proven the prerequisites.
- Permit at most two production PRs before the tracer, and prefer zero or one.
- Do not pre-create a broad horizontal issue forest that delays user-observable value.

## Product V1 And Issue Readiness

Grace defaults to Product V1 unless the tracked work explicitly selects another profile.

Use the Product V1 capability budget:

- one user-visible outcome;
- one primary invariant family;
- one supported environment and topology;
- one primary authority at each decision point;
- at most one new durable partial-state lifecycle;
- explicit exclusion of optional automation and generalized recovery.

Use the compact issue template and Issue Readiness gate in `docs/Development process.md`. Do not paste every possible
risk surface into every issue.

For stateful, destructive, filesystem, retry, recovery, concurrent, background, or multi-authority work, require the
`dev-process` Algorithm Readiness Gate and the `prototype` algorithm-witness branch before production coding.

## Branch And Worktree Mechanics

For a standalone issue:

1. fetch current `origin/main`;
2. create `agent/<issue-number>-<slug>` from `origin/main`;
3. create or use an issue-owned worktree;
4. target the pull request to `main`;
5. use `Closes #<issue>` when merge should close the issue.

For an epic:

1. create `epic/<parent-issue>-<slug>` from current `origin/main`;
2. create child issue branches from the current epic branch;
3. target child pull requests to the epic branch;
4. use `Related to #<child>` or `Part of #<parent>` for non-default-branch PRs;
5. keep the epic branch refreshed from `origin/main`;
6. use the final epic-to-`main` PR as the release candidate.

Parallelize only when product outcomes, authority models, write sets, and integration paths are independently safe.
Serialize work that touches shared project files such as `*.fsproj`, `Startup.Server.fs`, shared fixtures, generated
registries, or the same contract files.

When an assigned worktree differs from the session root, use absolute paths under the assigned worktree for patches and
verify git status in both locations after the first edit.

## Factory Run Topology

Freeze a Factory Run Charter before coding:

- one short-lived controller for one issue or PR;
- one active issue-owner implementation worker;
- no nested agent spawning;
- one read-only R1 discovery reviewer;
- one read-only R2 closure reviewer only when R1 produced accepted repairs;
- no process-rule changes inside the run.

The controller may inspect source, diffs, logs, tests, and validation evidence. The issue owner implements and repairs.
Do not start a fresh worker for each finding. Do not keep one root controller alive across an entire epic.

Default to one active high-risk Grace epic or factory-calibration stream. Additional Tier 2 streams require explicit
owner approval and independent authority and write-set proof.

## Bounded Review

Use `dev-process/CODE_REVIEW.md`.

1. Push one coherent candidate.
2. Run one R1 discovery review of the supported world and freeze its finite ledger.
3. If R1 passes and final-head GitHub `Validate` passes, no R2 is required.
4. When R1 has accepted findings, route one consolidated repair pass to the same issue owner.
5. Run one R2 closure review of accepted ledger items, repair hunks, direct repair regressions, final-head proof, and
   scope preservation.
6. Do not reopen untouched portions of the original diff during R2.
7. There is no automatic R3. If R2 incidentally exposes a supported-world merge blocker outside the frozen ledger and
   direct repair-regression scope, record one `DISCOVERY ESCAPE` and stop. Do not ignore it or keep searching. A new
   material invariant, authority boundary, state machine, product semantic, or scope expansion also requires a new
   owner-approved charter, split, or supersession.

Use a fresh, read-only review context that receives the complete run packet instead of inheriting hidden implementation
conversation. A repository or client may choose its strongest suitable review model and isolation controls, but durable
workflow guidance must not depend on a particular model name or client setting.

## Validation Profiles

Use the profile selected in `docs/Development process.md`:

| Profile | Primary surface |
| --- | --- |
| `docs-only` | Markdown, HTML, static guidance, and workflow docs |
| `domain-contract` | Types, DTOs, validators, serializers, hashes, and shared helpers |
| `cli-command` | Grace CLI behavior |
| `server-api` | HTTP handlers, server services, auth, persistence boundaries, and API contracts |
| `actor-workflow` | Orleans actor behavior and durable state transitions |
| `sdk-client` | SDK and client contracts |
| `deployment-runtime` | Aspire, emulators, Docker, Azure resources, scripts, and runtime configuration |

Focused proof comes first. GitHub `Validate` is the required broad gate for the final PR head. Use local Fast as an
explicit broad preflight and Full for local integration reproduction or diagnosis, not as routine duplicate work.

Common commands:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
npx --yes markdownlint-cli2 "**/*.md"
git diff --check
```

Format touched F# files before build and test proof. Build the focused project before a `--no-build` test.

## Pull Request Evidence

Keep the PR compact:

- linked issue and user-visible outcome;
- supported world, quality contract, and explicit non-goals;
- primary invariant and algorithm-witness result;
- changed paths and contract propagation;
- focused proof and final-head CI;
- R1 verdict and frozen ledger;
- repair commits and R2 closure verdict when applicable;
- residual risks, skipped proof, and recovery notes.

Do not append an agent transcript or one status comment per internal action.

## Merge Cleanup

After merge or intentional closure:

1. verify the destination contains the change;
2. confirm no uncommitted or unpushed task work is stranded;
3. remove the task worktree;
4. delete local and remote issue branches when appropriate;
5. run `git fetch --prune`;
6. update the local destination branch with `git pull --ff-only`;
7. update the issue and epic checklist;
8. record residual follow-ups that are real and explicitly out of scope.

## Owner Stops

Stop and use `dev-process` when:

- a second primary invariant, durable state machine, or authority boundary appears;
- a new product semantic or public contract is needed;
- the algorithm witness is contradicted;
- a third enabling PR would precede the tracer;
- review is defining lifecycle, ordering, recovery, or product behavior;
- process rules must change during the run;
- the current PR should be simplified, split, or superseded.
