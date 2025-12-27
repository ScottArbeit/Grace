# Grace.CLI.Tests Agents Guide

Read `../AGENTS.md` for global expectations before updating CLI tests.

## Purpose
- Validate CLI-specific helpers and behaviors without requiring server access.
- Prefer unit tests that exercise parsing, redaction, and history storage logic directly.

## Key Patterns
1. Keep tests isolated: back up and restore any files touched under `~/.grace`.
2. Use FsUnit for assertions and FsCheck for property-based coverage when appropriate.
3. Prefer deterministic timestamps for history-related tests.

## Validation
- Run `dotnet test --no-build` after changes.
- If build issues appear, run `dotnet build --configuration Release` first.
