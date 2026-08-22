# Grace Workflow

This reference is a Grace-specific router. It does not duplicate the portable development process or the complete
repository workflow.

## Current Sources Of Truth

Use, in order:

1. current owner request and decisions;
2. root and nearest `AGENTS.md`;
3. `docs/Development process.md`;
4. installed `dev-process` skill, including `QUALITY-CONTRACTS.md`, `CODE_REVIEW.md`, and `TEMPLATES.md`;
5. the linked issue, pull request, and canonical specification; and
6. current source, tests, contracts, generated artifacts, CI, and GitHub state.

`dev-process` owns quality contracts, capability budgets, algorithm readiness, tracer selection, epics and checkpoint
sessions, agent topology, context propagation, worker continuation, bounded R1/R2 review, and owner stop classification.
This file records Grace repository mechanics and routing reminders.

## Planning, Epics, And Tracked Work

- Planning-only requests stay in chat or the canonical specification.
- Create GitHub issues, branches, worktrees, or pull requests only when the user requests tracked work or implementation.
- Use GitHub issues and pull requests as the durable implementation coordination surfaces.
- Keep epics as durable product outcomes, accepted decisions, current small DAGs, and integration status.
- Do not keep an agent controller alive across an epic.
- Use a fresh epic-checkpoint session before the first child, after each merged child, and at epic closure.
- Create only the earliest tracer and prerequisites proven necessary by current evidence.
- Permit at most two production PRs before the tracer, and prefer zero or one.
- Select at most one next Tier 2 child at a checkpoint unless the owner approves truly independent streams.

## Product V1 And Issue Readiness

Grace defaults to Product V1 unless tracked work explicitly selects another profile.

Use the Product V1 capability budget:

- one value-bearing outcome;
- one primary invariant family;
- one supported environment and topology;
- one primary authority at each decision point;
- at most one new durable partial-state lifecycle; and
- explicit exclusion of optional automation and generalized recovery.

Use the compact issue template and Issue Readiness gate in `docs/Development process.md`. Stateful, destructive,
filesystem, retry, recovery, concurrent, background, or multi-authority work requires the `dev-process` Algorithm
Readiness Gate and a disposable executable witness before production coding.

## Delivery Modes

Choose the delivery mode separately from the issue hierarchy.

### Mainline slice, default

1. fetch current `origin/main`;
2. prove the base is admissible;
3. create `agent/<issue-number>-<slug>` from `origin/main`;
4. target the PR to `main`; and
5. use `Closes #<issue>` when merge should close the issue.

Use this mode for an independently correct slice that is safe or inert until later capability consumes it. A child may
belong to an epic and still use mainline mode.

### Epic integration branch, exception

Use this only after an owner decision records why partial slices cannot safely merge to `main`, CI coverage for
`epic/**`, refresh and admissibility policy, eventual delivery-delta review, and the final release candidate.

1. create `epic/<parent-issue>-<slug>` from current `origin/main`;
2. prove it remains admissible after every material refresh;
3. create child branches from the current epic branch;
4. target child PRs to the epic branch with non-closing wording;
5. inspect both child delta and eventual delivery delta against `main`; and
6. use the final epic-to-`main` PR as the release candidate.

Do not use an epic branch merely to mirror the parent issue or preserve stale ancestry.

## Baseline Admissibility And Salvage

Before semantic work:

- pin the exact base SHA and eventual delivery target;
- run required restore, build, and integration checks;
- inspect issue delta and eventual delivery delta against `main`;
- classify merge and rebase conflicts; and
- distinguish source-level salvage from ancestry requirements.

Stop before resolving conflicts that choose project shape, solution membership, package policy, generated output,
runtime topology, language migration, persistence, authorization, or public contracts. A focused project test does not
certify a newly composed solution or topology.

Prior branches, commits, and reviewed PRs are evidence and salvage sources. Transplant selected code, tests, and decisions
when safer than merging the lineage. Do not preserve ancestry unless an explicit compatibility requirement demands it.

When an assigned worktree differs from the session root, use absolute paths under the assigned worktree for patches and
verify git status in both locations after the first edit.

## Factory Run And Subagents

Freeze one Factory Run Charter per issue or PR and select one execution mode:

- **direct single-agent:** the issue root is the implementation owner and creates no implementation child;
- **controller/worker:** the issue root coordinates one continuing implementation worker thread;
- zero diagnostic scouts by default, with one read-only scout when root cause is unknown;
- one read-only R1 reviewer;
- one R2 review only after accepted repairs, preferably by resuming R1;
- no nested agent spawning; and
- no process, delivery-mode, or base-strategy changes inside the run.

Use direct mode for Tier 0 and bounded Tier 1 work with a stable contract, one worktree, and no expected algorithm,
topology, authority, or delivery-mode decision. Use controller/worker mode for Tier 2, factory calibration, stateful or
destructive behavior, complicated integration, or work expected to span implementation, CI, and repair turns.

Only the issue root spawns. Project custom child roles live under `.codex/agents/` and disable multi-agent tools.

For tracked implementation and review:

- use `fork_turns = "none"` for a new worker, scout, replacement, R1, or fresh R2;
- send a complete Subagent Run Packet;
- permit a positive integer only for a read-only scout that needs the immediately preceding failure exchange, normally
  `"1"` or `"2"`; and
- never use `fork_turns = "all"`.

Require every child to acknowledge role, SHA, workspace, scope, non-goals, stop conditions, and proof mode before work.

One implementation owner means one continuing identity, not one turn. Continue the direct-mode root or resume the
controller/worker-mode worker for compiler and focused test corrections, owned-path CI failures, self-review defects, and
one consolidated accepted R1 repair ledger.

At a material blocker choose exactly one action:

1. continue the same implementation owner;
2. dispatch one read-only diagnostic scout;
3. in controller/worker mode, use one replacement because the original worker thread is unavailable or unusable;
4. stop for an owner decision; or
5. supersede or split with a salvage map.

Do not ask for generic permission to add another worker.

## Bounded Review

Use `dev-process/CODE_REVIEW.md`.

1. Push one coherent candidate with current proof.
2. Run one R1 discovery review with no inherited implementation turns and freeze its finite ledger.
3. If R1 passes and final-head GitHub `Validate` passes, no R2 is required.
4. Continue the same implementation owner for one consolidated accepted repair pass.
5. Resume R1 for R2 when available, otherwise use one fresh read-only closure reviewer.
6. R2 checks the ledger, repair diff, direct regressions, final proof, delivery delta, and non-goals.
7. There is no automatic R3. A `DISCOVERY ESCAPE` or new semantic stops the run.

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

Focused proof comes first. GitHub `Validate` is the required broad gate for the final PR head. For a newly composed or
refreshed integration base, complete the broad admissibility gate before semantic work and before R1.

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

- linked issue and outcome;
- delivery mode, base SHA, delivery target, and Baseline Admissibility;
- supported world, quality contract, and non-goals;
- primary invariant and algorithm-witness result;
- issue delta and eventual delivery delta against `main`;
- changed paths and contract propagation;
- focused proof and final-head CI;
- R1 verdict and frozen ledger;
- repair commits and R2 result when applicable; and
- residual risks, skipped proof, and recovery notes.

Do not append an agent transcript or one status comment per internal action.

## Merge, Checkpoint, And Cleanup

After merge or intentional closure:

1. verify the destination contains the change;
2. confirm no task work is stranded;
3. remove the task worktree and delete local and remote issue branches when appropriate;
4. fetch and prune, then update local `main`;
5. update the issue and epic; and
6. end the issue controller.

For an epic child, start a fresh epic-checkpoint session to read current `main`, the merged PR, the parent issue, and the
canonical specification. Select at most one next Tier 2 child.

## Owner Stops

Stop and use the `dev-process` Decision Packet when:

- the base or integration result is not admissible;
- a semantic merge or rebase conflict requires an architectural choice;
- the eventual delivery delta exceeds the current capability budget;
- preserving ancestry imports deferred capability;
- a second primary invariant, durable state machine, authority boundary, or public semantic appears;
- the algorithm witness is contradicted;
- a third enabling PR would precede the tracer;
- review is defining lifecycle, ordering, recovery, or product behavior; or
- process or delivery mode must change during the run.

The packet must include the exact tracker, branch, worktree, and file effects of the recommended decision and the exact
next command or prompt after approval.
