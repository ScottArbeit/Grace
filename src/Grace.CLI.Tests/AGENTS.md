# Grace.CLI.Tests Agents Guide

Read `../AGENTS.md` for global expectations before updating CLI tests.

## Purpose

- Validate CLI command behavior and helpers without requiring server access.
- Keep tests deterministic and focused on parsing, command routing, output shaping, and local history/config behavior.

## Test File Organization

- Match test files to CLI command files where practical:
  - `Grace.CLI/Command/Agent.CLI.fs` -> `Grace.CLI.Tests/Agent.CLI.Tests.fs`
  - `Grace.CLI/Command/PromotionSet.CLI.fs` -> `Grace.CLI.Tests/PromotionSet.CLI.Tests.fs`
  - `Grace.CLI/Command/Queue.CLI.fs` -> `Grace.CLI.Tests/Queue.CLI.Tests.fs`
  - `Grace.CLI/Command/Review.CLI.fs` -> `Grace.CLI.Tests/Review.CLI.Tests.fs`
  - `Grace.CLI/Command/WorkItem.CLI.fs` -> `Grace.CLI.Tests/WorkItem.CLI.Tests.fs`
  - Root command behavior belongs in `Grace.CLI.Tests/Program.CLI.Tests.fs`.
- Keep auth-focused tests separate for now (`Auth.Tests.fs`, `AuthTokenBundle.Tests.fs`).

## Key Patterns

1. Keep tests isolated: back up and restore any files touched under `~/.grace`.
2. Use FsUnit for assertions and FsCheck for property-based coverage when appropriate.
3. Prefer deterministic timestamps for history-related tests.

## Validation

- Run `dotnet build --configuration Release` if needed.
- Run `dotnet test --no-build --configuration Release` after changes.
