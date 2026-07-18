# Grace.CLI.Tests Agents Guide

Read `../AGENTS.md` for global expectations before updating CLI tests.

## Purpose

- Validate CLI command behavior and helpers without requiring server access.
- Keep tests deterministic and focused on parsing, command routing, output shaping, and local history/config behavior.
- Keep pure parse-only coverage separate from stateful command tests so NUnit can parallelize the safe subset.

## Test File Organization

- Match test files to CLI command files where practical:
  - `Grace.CLI/Command/Agent.CLI.fs` -> `Grace.CLI.Tests/Agent.CLI.Tests.fs`
  - `Grace.CLI/Command/PromotionSet.CLI.fs` -> `Grace.CLI.Tests/PromotionSet.CLI.Tests.fs`
  - `Grace.CLI/Command/Queue.CLI.fs` -> `Grace.CLI.Tests/Queue.CLI.Tests.fs`
  - `Grace.CLI/Command/Review.CLI.fs` -> `Grace.CLI.Tests/Review.CLI.Tests.fs`
  - `Grace.CLI/Command/WorkItem.CLI.fs` -> `Grace.CLI.Tests/WorkItem.CLI.Tests.fs`
- Root command behavior belongs in `Grace.CLI.Tests/Program.CLI.Tests.fs`.
- Pure parser tests belong in dedicated `*.CLI.Parsing.Tests.fs` files and should not touch invocation, console,
  environment, filesystem, current-directory, local config/history, or SDK identity state.
- Stateful command invocation, config, environment, console, filesystem, local-state, and SDK identity tests remain in
  the non-parsing files and stay serialized.
- Keep auth-focused tests separate for now (`Auth.Tests.fs`, `AuthTokenBundle.Tests.fs`).

## Key Patterns

1. Keep tests isolated: back up and restore any files touched under `~/.grace`.
2. Use FsUnit for assertions and FsCheck for property-based coverage when appropriate.
3. Prefer deterministic timestamps for history-related tests.
4. Do not add assembly-level NUnit parallel defaults here until the remaining global/current-process mutations are
   isolated or proven safe.

## Validation

- Run the matching Release build before any project-specific `dotnet test --no-build` command.
- Run `dotnet test --configuration Release --no-build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj` only after that build
  context exists.
- Run the smallest focused CLI proof first. Fast is an optional broad preflight; GitHub `Validate` certifies the current
  pull-request revision. Fast remains one solution-level `dotnet test` invocation with a filter rather than custom
  per-project process fan-out.
