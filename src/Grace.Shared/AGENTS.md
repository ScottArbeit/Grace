# Grace.Shared Agents Guide

Review `../AGENTS.md` for global policies before working in this project.

## Purpose
- Provide reusable utilities, extensions, and cross-cutting helpers consumed by other Grace services, SDKs, and tooling.
- Centralize shared logic so that features stay DRY and consistent across the solution.

## Key Patterns
- Organize helpers into modules; keep functions small, pure, and composable wherever possible.
- Use extension members sparingly and document side-effecting helpers with concise comments so intent stays clear.
- Maintain `InternalsVisibleTo` patterns that allow targeted testing without leaking unnecessary APIs.
- Coordinate semantic changes with dependents (`Grace.Server`, `Grace.SDK`, `Grace.CLI`, etc.) to avoid breaking downstream logic.

## Project Rules
1. Prefer adding new helpers instead of altering the semantics of widely used ones—create adapters when behavior must diverge.
2. When changing internals referenced by other projects, update those callers or provide a compatibility shim.
3. Record noteworthy patterns or usage notes right here so agents can operate with minimal additional code exploration.

## Validation
- Extend `Grace.Shared.Tests` (or add new fixtures) whenever helper behavior changes.
- Run `fantomas` on touched F# files to stay aligned with repo formatting.
- Execute `dotnet build --configuration Release` to confirm dependent projects continue to compile.
