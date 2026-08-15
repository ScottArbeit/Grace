# Grace Repository Agents Guide

Agents operating under `/src` should follow this playbook alongside the existing `AGENTS.md` in the repo root.
Treat this file as the canonical high-level brief; each project folder contains an `AGENTS.md` with deeper context.

## Local Commands

- `pwsh ./scripts/bootstrap.ps1`
- Focused project proof for the changed behavior; use Fast or Full only as optional broad local escalation.

Optional: `pwsh ./scripts/install-githooks.ps1` adds a staged-diff check only; focused tests and formatting stay explicit.

## Work Tracking

Use GitHub issues and pull requests as the active coordination surface for implementation work.
For non-trivial work, follow `../docs/Development process.md`: create or confirm a GitHub issue, declare objective,
owned paths, risk surfaces, validation, docs impact, and definition of done before editing. If the write set grows,
update the issue before editing the new paths.

## Core Engineering Expectations

- Make a multi-step plan for non-trivial work, keep edits focused, and leave code cleaner than you found it.
- When the user says `Plan <work item>`, plan the work in chat. Create a GitHub issue only when the user explicitly asks
  for one, asks to start tracked implementation, or otherwise requests tracker setup.
- For tracked multi-step implementation, follow `docs/Development process.md`: create an epic parent issue, link
  sub-issues for each implementation step, assign each sub-issue's parent issue relationship to the epic in GitHub
  Relationships, and include a DAG in the parent issue that shows dependencies and parallelization opportunities. As
  each sub-issue completes, update the epic checklist. Use the concrete `addSubIssue` GraphQL workflow in
  `docs/Development process.md` when creating the native parent/child relationships.
- For non-trivial epics, identify an early tracer-bullet vertical slice before broad parallelization. It should prove
  one narrow user-visible behavior through the closest stable public boundary, crossing the main contract, runtime,
  persistence, validation, and documentation surfaces that later slices are likely to reuse. Use the result to refine
  child issues, owned paths, validation profiles, and parallelization boundaries.
- When implementing an epic, always use an explicit epic integration branch. Create
  `epic/<parent-issue>-<slug>` from `origin/main`, branch sub-issue worktrees from the current `origin/epic/...`, open
  sub-issue PRs to the epic branch, keep that branch refreshed from `origin/main`, and use the final epic-to-`main` PR
  as the production release candidate. Do not use direct-to-`main` epic slices.
- Before assigning behavior-changing work, require the compact Issue Readiness gate from
  `docs/Development process.md`: one user-visible outcome, supported world, capability budget, primary invariant and
  authority, explicit non-goals, algorithm-witness result when required, focused proof, and owner stop conditions.

- For code tasks that implement a product spec or generated plan, verify decision closure before editing: accepted or
  assumed product decisions, supported/rejected inputs, audience/authorization semantics, failure behavior, lifecycle
  states, and contract propagation must be explicit in the issue or PR. If a critical decision is missing, update the
  issue or report the gap before coding.
- For runtime, storage, materialization, Watch, eventing, or auth work, write a stale-authority preflight in the worker
  handoff: what state is authoritative, when it is re-read, what can change between decision and mutation, how retries and
  cleanup behave, and which focused proof catches stale snapshots.

- Claim the issue with a comment, assign it to the authenticated GitHub user, and create or switch to an issue-owned
  branch/worktree from the selected base before editing implementation files: latest `origin/main` for standalone
  non-epic issues, or current `origin/epic/...` for sub-issues under the required epic integration branch.
- When a task assigns a worktree different from the thread workspace root, every `apply_patch` filename must be an
  absolute path under the assigned worktree. After the first patch, verify git status in both locations.
- Prefer vertical slices that prove one public behavior at a time through the closest stable boundary.
- Validate the changed behavior locally with the smallest focused proof. Format touched F# files first, build the
  focused project before a `--no-build` test, run required freshness checks, and finish with `git diff --check`.
- GitHub `Validate` is the required broad gate for the current pull-request revision. Local Fast is an optional broad
  preflight; Full is for local integration reproduction or diagnosis, not a routine consequence of touching a runtime
  surface. If either broad command is intentionally selected, do not duplicate its build/test work with routine focused
  build/test commands for that checkpoint.
- Product/DAG independence is not the same as merge/write-set independence. Parallelize branches only when their write
  sets are disjoint enough to avoid predictable churn. Serialize or merge-queue branches that touch shared project
  files such as `*.fsproj`, `Startup.Server.fs`, or the same test/helper files. For broad waves, consider a
  preparatory compile-item or file-scaffold slice before later branches edit separate files.
- Follow the Factory V2 orchestration and bounded review protocol in `../docs/Development process.md`. Do not copy a
  separate review loop into project-local guidance.
- Before Tier 2 production coding, confirm the Algorithm Readiness Gate is complete for stateful, destructive,
  filesystem, retry, recovery, concurrent, background, or multi-authority behavior. Production implementation should
  translate a proven finite algorithm rather than discover effect ordering through code review.
- Use one short-lived controller per issue or pull request. The controller is the sole agent spawner. Workers and
  reviewers must not spawn subagents.
- Use one issue-owner implementation worker. The same worker owns all accepted in-scope repairs. Do not create a fresh
  worker for each finding. A replacement requires evidence that the original worker is unavailable or unusable.
- The controller may inspect source, diffs, logs, tests, and validation evidence and may run read-only or validation
  commands. It coordinates issue and PR state, review ledger, CI, merge, and cleanup.
- Do not require temp status files or fixed five-minute heartbeats. Require concise progress updates before long-running
  commands, at material blockers, and at handoff. Never sleep or poll for more than 120 seconds in one command.
- Before handoff, the issue owner must self-review the actual diff against the outcome, non-goals, Product V1 supported
  world, authority and effect model, contract propagation, proof truth, and owned paths.
- Open one coherent ready-for-review PR after the first validated candidate. Run one independent R1 discovery review of
  the full candidate with `dev-process/CODE_REVIEW.md`. Freeze the accepted finite ledger.
- Route the accepted R1 ledger back to the same issue owner for one consolidated repair pass. Do not start another broad
  review after each repair commit.
- When repairs were required, run one independent R2 closure review on the repaired current head. R2 checks accepted
  ledger items, repair hunks, direct repair regressions, current-head proof and CI, and scope preservation. It must not
  reopen untouched parts of the original diff.
- There is no automatic R3. If R2 incidentally exposes a supported-world merge blocker outside the frozen ledger and
  direct repair-regression scope, record one `DISCOVERY ESCAPE` and stop. Do not ignore it or keep searching. A new
  material invariant, authority boundary, state machine, product semantic, or scope expansion also requires a new
  owner-approved charter, split, or supersession.
- GitHub `Validate` must pass on the final head. A current R1 pass with no findings plus final-head CI is sufficient;
  after repairs, R2 plus final-head CI certifies closure.
- When a required check fails, inspect the newest failed workflow for the final head and distinguish owned-diff failures
  from unrelated repository or environment failures before assigning repair work.
- Stop when the issue gains another primary invariant, durable state machine, authority boundary, or product semantic;
  when a third enabling PR is proposed before the tracer; when review is writing the product model; or when process rules
  must change mid-run. Preserve a salvage map before superseding.
- After an agent-owned pull request is merged, or closed because the related issue/sub-issue work is complete, cleanup
  is mandatory: verify the destination contains the change, delete the remote issue branch, delete the local issue
  branch, remove the task worktree, run `git fetch --prune`, and `git pull --ff-only` in the local repo so `main` is up
  to date. Do not wait for a separate user prompt before deleting the remote branch.
- Record skipped validation, docs impact, residual risk, and follow-ups in the task record or pull request.
- Write tests for new features and bug fixes; prioritize critical paths.
- Document new F# modules, types, functions, methods, members, and meaningful local helper functions with concise
  `///` XML comments so future maintainers and IntelliSense users understand their purpose.
- Treat secrets with care, avoid logging PII, and preserve structured logging (including correlation IDs).
- Favor existing helpers in `Grace.Shared` before adding new utilities.

## Test Project Organization

- `Grace.Server.Tests` is the Aspire-backed server integration project. Put HTTP flows, emulator/resource coverage,
  Service Bus validation, storage route behavior, and server-surface actor behavior there.
- `Grace.Server.Unit.Tests` is the no-Aspire server-adjacent project. Put pure helper, deterministic contract, and
  unit-shaped server coverage there only when it does not need HTTP hosting, emulators, blob storage, Service Bus,
  Redis, `Grace.Server.Tests.Services`, or `Grace.Aspire.AppHost`.
- `Grace.CLI/Command/*.CLI.fs` should be primarily covered by `Grace.CLI.Tests/*.CLI.Tests.fs`. Pure parser coverage
  belongs in dedicated `*.CLI.Parsing.Tests.fs` files that can be parallelized. Tests that touch command invocation,
  local config/history, environment variables, console output, filesystem/current-directory state, or SDK identity
  remain serialized in the non-parsing CLI test files.
- `Grace.Types/*.Types.fs` should be covered by `Grace.Types.Tests/*.Types.Tests.fs`.
- Keep auth-focused suites separate for now (`Grace.Authorization.Tests`, plus auth-specific files inside other test
  projects).
- Prefer server-surface integration tests for actor behavior; avoid duplicating deep actor internals in server test files.

## Test Parallelization And Validation

- `pwsh ./scripts/validate.ps1 -Fast` uses one solution-level `dotnet test "src/Grace.slnx"` command with the selected
  non-Aspire filter: Authorization, CLI, Operations, Types, and Server.Unit tests. `-Full` uses one unfiltered
  solution-level command, so every current and future test project in `src/Grace.slnx` runs. GitHub `Validate` reuses
  this Full selection implementation rather than maintaining its own test list.
- Do not reintroduce custom per-project process fan-out into validation unless a future issue owns that runner change.
- Assembly-level NUnit parallel defaults are intentionally limited. `Grace.Authorization.Tests` and `Grace.Types.Tests`
  have bounded defaults. `Grace.Server.Unit.Tests` is deferred while process-static approval-store mutation remains in
  the project. `Grace.CLI.Tests` is deferred while global/current-process mutations remain. `Grace.Server.Tests` stays
  integration-controlled because it shares Aspire-hosted resources and setup state.
- If running a project-specific `dotnet test --no-build` command, run the matching Release build for that project first
  so the test assembly exists and reflects current source.

## F# Coding Guidelines

- Default to F# for new code unless stakeholders specify another language.
- Use `task { }` for asynchronous workflows and keep side effects isolated.
- Prefer immutable data, small pure functions, and explicit dependencies passed as parameters.
- Prefer collections from `System.Collections.Generic` (for example `List<T>`, `Dictionary<K,V>`) over F#-specific
  collections unless pattern matching or discriminated unions are needed.
- Apply the modern indexer syntax (`myList[0]`) for lists, arrays, and sequences; avoid the legacy `.[ ]` form.
- Structure modules so domain types live in `Grace.Types`, shared helpers in `Grace.Shared`, and orchestration in the
  project-specific assembly.
- Treat `///` XML documentation comments as required for new F# declaration surfaces: modules, types, functions,
  methods, members, and meaningful local helper functions. Keep these comments concise and purpose-focused.
- Do not satisfy the XML documentation rule with boilerplate such as "performs X", "returns X", "creates X", or
  "for the current operation/request" when that repeats the declaration name. Describe the concrete Grace behavior,
  invariant, route, command, state transition, storage operation, validation rule, or contract role instead.
- Add ordinary inline comments only where control flow or transformations are non-obvious.
- Run Fantomas formatting or a targeted Fantomas check before build and test validation. Avoid the slow loop where tests
  pass, Fantomas then changes files, and the same build/tests must be repeated. For broad F# edits, format with
  `dotnet tool run fantomas --recurse .` from `./src`; for narrow fixes, run targeted Fantomas on the touched files
  before validation.

## Avoid FS3511 in Resumable Computation Expressions

These rules apply to `task { }` and `backgroundTask { }`.

1. **Do not define `let rec` inside `task { }`.**
2. **Avoid `for ... in ... do` loops inside `task { }`.**
3. **Treat FS3511 warnings as regressions; do not suppress them.**

## Agent-Friendly Context Practices

- Start with relevant `AGENTS.md` files to load patterns, dependencies, and test strategy before broad code exploration.
- When Agent Skills are available, load `../skills/grace/SKILL.md` after the relevant `AGENTS.md` files and use its
  references as the on-demand router for Grace-specific workflow, architecture, testing, and public-surface guidance.
- Use these summaries to target only source files needed for implementation or verification.
- When documenting new behavior, update the closest `AGENTS.md` so future agents inherit context quickly.

## Collaboration and Communication

- Summarize modifications clearly, cite file paths with 1-based line numbers, and call out remaining follow-ups/tests.
- Coordinate cross-project changes across `Grace.Types`, `Grace.Shared`, `Grace.Server`, `Grace.Actors`, `Grace.CLI`,
  and `Grace.SDK`.
- When adding capabilities, ensure matching tests exist and note any residual risk.
