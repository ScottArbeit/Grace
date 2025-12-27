# Grace.CLI Agents Guide

Read `../AGENTS.md` for global expectations before updating CLI code.

## Purpose
- Provide developer tooling entry points that wrap Grace services via System.CommandLine and Spectre.Console.
- Surface Grace workflows through friendly commands while keeping logic reusable for tests and automation.

## Key Patterns
- Define command arguments and options in dedicated `Options` modules that produce `Option<'T>` values.
- Create handlers with `CommandHandler.Create(...)` that delegate to reusable services (often via `Grace.SDK`).
- Use Spectre.Console for user interaction, but keep core logic testable and separate from console presentation.
- Maintain alignment with DTOs and contracts defined in `Grace.Types`.

## Project Rules
1. Keep handlers thin; move heavier logic into services or helpers that are straightforward to unit test.
2. Preserve existing option names/switches; introduce new aliases when expanding behavior instead of breaking existing scripts.
3. Capture new command patterns or usage tips in this document to guide future agents.

## Recent Patterns
- `grace history` commands operate without requiring a repo `graceconfig.json`; avoid `Configuration.Current()` in history-related flows.

## Validation
- Add option parsing tests and handler unit tests for new functionality.
- Manually exercise impacted commands when practical and ensure `dotnet build --configuration Release` stays green.

## Command Modules (`Grace.CLI.Command`)
- Parameter classes usually derive from `ParameterBase()`; keep them lightweight and validated at construction.
- Organize command-specific helpers under `Options` modules and wrap invocation with `CommandHandler.Create`.
- Enforce that command parsing remains thin—push complex behavior into services so tests can cover edge cases.
- Add parsing and handler behavior tests in tandem with any new command implementation.
