# Grace.Shared — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
Shared utilities, extensions, and cross-cutting helpers.

## Key patterns
- Module-based helpers and `.NET` interop extension functions.
- Small pure functions; side-effecting helpers are explicit.
- Internal visibility used for tests (`InternalsVisibleTo` patterns).

## Project rules for agents
1. Prefer adding new helpers rather than changing semantics of existing ones.
2. If changing internals used by other projects, update callers or provide a fallback adapter.
3. Add unit tests to `Grace.Shared.Tests` for behavioral changes.

## Serialization & compatibility
- Types here are commonly referenced; avoid changing public/shared records or DUs used elsewhere.
