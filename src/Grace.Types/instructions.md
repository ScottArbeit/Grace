# Grace.Types — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
Domain types, events, and DTOs used in transport and persistence.

## Key patterns
- Use discriminated unions (DUs) and records as canonical domain models.
- Types are annotated for serializers (MessagePack, System.Text.Json) and must retain compatibility.

## Project rules for agents
1. For schema changes, create versioned types (e.g., `XxxV2`) and migration adapters.
2. Add round-trip serialization tests for each serializer the repo uses.
