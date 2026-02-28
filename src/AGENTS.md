# Grace Repository Agents Guide

Agents operating under `D:\Source\Grace\src` should follow this playbook alongside the existing `AGENTS.md` in the repo root.
Treat this file as the canonical high-level brief; each project folder contains an `AGENTS.md` with deeper context.

## Local Commands

- `pwsh ./scripts/bootstrap.ps1`
- `pwsh ./scripts/validate.ps1 -Fast` (use `-Full` for Aspire integration tests)

Optional: `pwsh ./scripts/install-githooks.ps1` to add a pre-commit `validate -Fast` hook.

## Work Tracking

Do not use beads/`bd` by default.
Track implementation progress in the task-specific plan file (for example `CodexPlan.md`) and keep git history granular.

## Core Engineering Expectations

- Make a multi-step plan for non-trivial work, keep edits focused, and leave code cleaner than you found it.
- Validate changes with `pwsh ./scripts/validate.ps1 -Fast` (use `-Full` for Aspire integration coverage).
- If running commands manually, use `dotnet build --configuration Release` and `dotnet test --no-build`.
- Resolve all compilation errors before considering a task complete.
- Run impacted tests for each task and fix failures introduced by your changes.
- Create a new git commit after each completed task to keep review scope clear.
- Write tests for new features and bug fixes; prioritize critical paths.
- Document new public APIs with XML comments and update nearby `AGENTS.md`/docs when behavior changes.
- Treat secrets with care, avoid logging PII, and preserve structured logging (including correlation IDs).
- Favor existing helpers in `Grace.Shared` before adding new utilities.

## Test Project Organization

- `Grace.Server/*.Server.fs` should be primarily covered by `Grace.Server.Tests/*Server.Tests.fs`.
- `Grace.CLI/Command/*.CLI.fs` should be primarily covered by `Grace.CLI.Tests/*.CLI.Tests.fs`.
- `Grace.Types/*.Types.fs` should be covered by `Grace.Types.Tests/*.Types.Tests.fs`.
- Keep auth-focused suites separate for now (`Grace.Authorization.Tests`, plus auth-specific files inside other test projects).
- Prefer server-surface integration tests for actor behavior; avoid duplicating deep actor internals in server test files.

## F# Coding Guidelines

- Default to F# for new code unless stakeholders specify another language.
- Use `task { }` for asynchronous workflows and keep side effects isolated.
- Prefer immutable data, small pure functions, and explicit dependencies passed as parameters.
- Prefer collections from `System.Collections.Generic` (for example `List<T>`, `Dictionary<K,V>`) over F#-specific collections unless pattern matching or discriminated unions are needed.
- Apply the modern indexer syntax (`myList[0]`) for lists, arrays, and sequences; avoid the legacy `.[ ]` form.
- Structure modules so domain types live in `Grace.Types`, shared helpers in `Grace.Shared`, and orchestration in the project-specific assembly.
- Add lightweight comments only where control flow or transformations are non-obvious.
- Format code with `dotnet tool run fantomas --recurse .` from `./src`.

## Avoid FS3511 in Resumable Computation Expressions

These rules apply to `task { }` and `backgroundTask { }`.

1. **Do not define `let rec` inside `task { }`.**
2. **Avoid `for ... in ... do` loops inside `task { }`.**
3. **Treat FS3511 warnings as regressions; do not suppress them.**

## Agent-Friendly Context Practices

- Start with relevant `AGENTS.md` files to load patterns, dependencies, and test strategy before broad code exploration.
- Use these summaries to target only source files needed for implementation or verification.
- When documenting new behavior, update the closest `AGENTS.md` so future agents inherit context quickly.

## Collaboration and Communication

- Summarize modifications clearly, cite file paths with 1-based line numbers, and call out remaining follow-ups/tests.
- Coordinate cross-project changes across `Grace.Types`, `Grace.Shared`, `Grace.Server`, `Grace.Actors`, `Grace.CLI`, and `Grace.SDK`.
- When adding capabilities, ensure matching tests exist and note any residual risk.
