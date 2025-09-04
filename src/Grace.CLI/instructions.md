# Grace.CLI — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
CLI entrypoints and command implementations (System.CommandLine, Spectre.Console).

## Key patterns
- `Options` modules that define `Option<T>` objects.
- Command handlers created with `CommandHandler.Create(...)` calling into `Grace.SDK` or services.
- Spectre.Console used for progress/visuals; keep logic testable and separate from UI surfaces.

## Project rules for agents
1. Add parse tests for options and unit tests for handler logic.
2. Do not change existing option names/switches without deprecation.

## Grace.CLI.Command

### Purpose
Detailed CLI command modules and patterns.

### Key patterns
- Parameter classes derived from `ParameterBase()`.
- `Options` modules and `CommandHandler.Create` wrappers.
- Use `Spectre.Console` for user interaction.

### Project rules for agents
1. Keep parsing logic thin; heavy logic should be extracted to services that can be unit tested.
2. Add tests for parsing and handler behaviors.
