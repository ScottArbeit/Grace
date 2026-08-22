# Grace Source Agent Guide

Agents operating under `src/` must follow the repository-root `AGENTS.md`, `docs/Development process.md`, and this
source-specific guide. Project-level `AGENTS.md` files add narrower context for their subtrees.

The root process documents own issue readiness, delivery mode, branch and worktree mechanics, controller and subagent
topology, review, merge, and cleanup. This file does not create a second workflow.

## Local Commands

- `pwsh ./scripts/bootstrap.ps1`
- Run the smallest meaningful focused proof for the changed behavior.
- Format touched F# files before build and test validation.
- Finish each coherent checkpoint with `git diff --check`.

Use `pwsh ./scripts/validate.ps1 -Fast` only as an optional broad local preflight. Use `-Full` for local integration
reproduction or diagnosis. GitHub `Validate` is the required broad gate for the current pull-request revision.

Optional: `pwsh ./scripts/install-githooks.ps1` adds a staged-diff check only. Focused tests and formatting remain
explicit responsibilities.

## Tracked Source Work

- Read the root and nearest `AGENTS.md` files before editing, then load `../skills/grace/SKILL.md` as the Grace context
  router when Agent Skills are available.
- Implement only from an issue that has passed the compact Issue Readiness gate and has a frozen Factory Run Charter.
- Use the delivery mode and exact base SHA from the Run Charter. A child of an epic normally branches from current
  `origin/main`; use an `epic/**` base only when the owner selected integration-branch mode.
- Prove Baseline Admissibility before semantic edits. Restore, build, or run the integration proof selected by the
  charter. A conflict involving project or solution shape, package policy, generated surfaces, runtime topology,
  language migration, persistence, authorization, or public contracts is an owner stop unless the issue already
  specifies the exact resolution.
- Treat earlier branches and commits as salvage evidence, not mandatory ancestry, unless the issue names a current
  compatibility requirement.
- Declare the owned and forbidden paths before editing. Update the issue and stop for an owner decision before crossing
  the frozen write set for a material project, topology, contract, state, or authority change.
- Use the execution mode frozen in the Run Charter. In direct single-agent mode, the root owns implementation and no
  implementation child is created. In controller/worker mode, keep one issue-owner worker thread and resume it for
  in-scope compiler, focused-test, formatting, and accepted R1 repair work. Do not start a fresh worker per error or
  finding.
- Only the root issue session may spawn agents. Always specify `fork_turns`; do not rely on its current full-history
  default. New workers, scouts, and reviewers use `fork_turns = "none"` plus a complete Run Packet. A read-only
  diagnostic scout may use the smallest positive turn count only when the immediately preceding failure exchange is
  required evidence. Source work never uses `fork_turns = "all"`.
- At a blocker, use the typed action model from the root process: continue the same implementation owner, one read-only
  diagnostic scout, one replacement for an unavailable or unusable worker in controller/worker mode, owner stop, or
  supersede/split. Do not ask for an unclassified “one more worker.”
- The implementation owner performs focused validation and a review-prevention self-check of the actual diff. In
  controller/worker mode, the root may inspect code, diffs, logs, tests, and validation evidence, but it must not silently
  replace the worker's implementation role.
- Run one R1 Discovery Review of the coherent candidate. Route the finite accepted ledger to the same implementation
  owner in one consolidated repair pass. Run R2 Closure Review only when repairs occurred. There is no automatic R3.
- Inspect both the issue delta against its selected base and the eventual delivery delta against `main`. Capability
  already present in an epic base still counts as delivered capability.
- Never sleep or poll for more than 120 seconds in one command. Use repeated shorter checks with concise updates at
  material transitions.

## Core Engineering Expectations

- Prefer a vertical slice that proves one observable behavior through the closest stable boundary.
- Keep effects isolated, authorities explicit, and retry, cleanup, and stale-state behavior visible.
- For runtime, storage, materialization, Watch, eventing, or authorization work, identify the authoritative source, the
  revalidation point before mutation or publication, the failure state, retry and cleanup behavior, and the proof that a
  stale snapshot cannot win.
- Resolve compilation errors and failures introduced by the changed slice before handoff.
- Build the focused project before using `dotnet test --no-build`.
- Commit coherent completed checkpoints. Do not create commits merely to record a broken intermediate state unless the
  Run Charter requires a deliberate RED proof.
- Write tests for new features and bug fixes, prioritizing positive, negative, restart, boundary, and direct regression
  behavior selected by the issue contract.
- Record skipped validation, documentation impact, residual risk, and follow-up ownership in the issue or pull request.
- Treat secrets with care, avoid logging personally identifiable information, and preserve structured logging and
  correlation identifiers.
- Favor existing helpers in `Grace.Shared` before adding new utilities.

## Test Project Organization

- `Grace.Server.Tests` is the Aspire-backed server integration project. Put HTTP flows, emulator and resource coverage,
  Service Bus validation, storage route behavior, and server-surface actor behavior there.
- `Grace.Server.Unit.Tests` is the no-Aspire server-adjacent project. Put pure helper, deterministic contract, and
  unit-shaped server coverage there only when it does not need HTTP hosting, emulators, blob storage, Service Bus,
  Redis, `Grace.Server.Tests.Services`, or `Grace.Aspire.AppHost`.
- `Grace.CLI/Command/*.CLI.fs` should be primarily covered by `Grace.CLI.Tests/*.CLI.Tests.fs`. Put pure parser coverage
  in dedicated `*.CLI.Parsing.Tests.fs` files that can run in parallel. Keep command invocation, local config and
  history, environment variables, console output, filesystem and current-directory state, and SDK identity tests in
  the serialized non-parsing CLI test files.
- `Grace.Types/*.Types.fs` should be covered by `Grace.Types.Tests/*.Types.Tests.fs`.
- Keep authorization-focused suites separate for now, including `Grace.Authorization.Tests` and authorization-specific
  files inside other test projects.
- Prefer server-surface integration tests for actor behavior. Avoid duplicating deep actor internals in server tests.

## Test Parallelization And Validation

- `pwsh ./scripts/validate.ps1 -Fast` uses one solution-level `dotnet test "src/Grace.slnx"` command with the selected
  non-Aspire filter: Authorization, CLI, Operations, Types, and Server.Unit tests.
- `-Full` uses one unfiltered solution-level command, so every current and future test project in `src/Grace.slnx` runs.
  GitHub `Validate` reuses this Full selection instead of maintaining a separate project list.
- Do not reintroduce custom per-project process fan-out unless a future issue owns that runner change.
- Assembly-level NUnit parallel defaults are intentionally limited. `Grace.Authorization.Tests` and
  `Grace.Types.Tests` have bounded defaults. `Grace.Server.Unit.Tests` remains deferred while process-static approval
  store mutation exists. `Grace.CLI.Tests` remains deferred while global and current-process mutation exists.
  `Grace.Server.Tests` remains integration-controlled because it shares Aspire-hosted resources and setup state.

## F# Coding Guidelines

- Default to F# for new source unless the accepted issue chooses another language.
- Use `task { }` for asynchronous workflows and keep side effects isolated.
- Prefer immutable data, small pure functions, and explicit dependencies passed as parameters.
- Prefer `System.Collections.Generic` collections, such as `List<T>` and `Dictionary<K,V>`, unless F# collections add
  meaningful pattern-matching or discriminated-union value.
- Use modern indexer syntax such as `myList[0]`; avoid the legacy `.[ ]` form.
- Keep domain types in `Grace.Types`, reusable helpers in `Grace.Shared`, and orchestration in the owning project.
- Add concise `///` XML documentation to new modules, types, functions, methods, members, and meaningful local helpers.
  Describe the concrete Grace behavior, invariant, route, command, state transition, storage operation, validation rule,
  or contract role. Do not restate the declaration name with boilerplate.
- Add ordinary inline comments only where control flow or transformations are non-obvious.
- Run Fantomas on touched files before build and tests. For broad F# edits, use
  `dotnet tool run fantomas --recurse .` from `src`; for narrow edits, format only the touched files.

## Avoid FS3511 In Resumable Computation Expressions

These rules apply to `task { }` and `backgroundTask { }`:

1. Do not define `let rec` inside the computation expression.
2. Avoid `for ... in ... do` loops inside the computation expression.
3. Treat FS3511 warnings as regressions; do not suppress them.

## Handoff

Summarize modifications with file paths and 1-based line numbers, report focused and broad proof truthfully, and call
out residual risks and follow-up ownership. Coordinate cross-project changes across `Grace.Types`, `Grace.Shared`,
`Grace.Server`, `Grace.Actors`, `Grace.CLI`, and `Grace.SDK` when the accepted issue requires them.
