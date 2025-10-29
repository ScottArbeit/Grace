# Grace.Types Agents Guide

Refer to `../AGENTS.md` for shared expectations before working here.

## Purpose
- Domain types, discriminated unions, events, and DTOs used in transport and persistence layers across the Grace ecosystem.
- Acts as the canonical schema source for actors, the server, CLI, and SDK components.

## Key Patterns
- Prefer discriminated unions and records for domain models; keep constructors simple and intention revealing.
- Ensure every type remains compatible with its serializers (MessagePack, System.Text.Json, and any others referenced in code).
- When altering schemas, add versioned records/DU cases (for example `FooV2`) and supply migration adapters or compatibility shims.
- Synchronize breaking or behavioral changes with consumers in `Grace.Shared`, `Grace.Server`, and `Grace.SDK` before merging PRs.
- Capture schema background and migration rationale inline (XML docs or comments) so future agents understand expectations without scanning history.

## Project Rules
1. Avoid direct breaking changes; introduce new versions and migrate callers gradually.
2. Keep serializer attributes accurate and up to date when adding fields or cases.
3. Document new versioning or migration strategies inside this file so subsequent agents load the context immediately.

## Validation
- Add round-trip tests for every serializer impacted by a change (MessagePack, STJ, etc.).
- Run `dotnet build --configuration Release` and any focused serialization/unit tests that cover the updated types.
