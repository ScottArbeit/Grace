# Grace.Actors — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
Orleans grain interface and implementations.

## Key patterns
- Grain interfaces and implementations follow Orleans activation patterns.
- State is often persisted via events in Grace.Types.

## Project rules for agents
1. Update `Grace.Orleans.CodeGen` (if required) when adding new types needing codegen.
2. Add grain activation tests and idempotency tests for state transitions.
