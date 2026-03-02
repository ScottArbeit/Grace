# Grace.Types Agents Guide

Refer to `../AGENTS.md` for shared expectations before working here.

## Purpose

- Domain types, discriminated unions, events, and DTOs used across actors, server, CLI, SDK, and persistence.
- Canonical schema source for shared contracts.

## Key Patterns

- Prefer records and discriminated unions with clear intent.
- Keep serializer attributes accurate (MessagePack, Orleans, System.Text.Json where applicable).
- Keep domain contracts free of server endpoint concerns.
- Synchronize schema changes with consumers in `Grace.Actors`, `Grace.Server`, `Grace.CLI`, and `Grace.SDK`.

## Project Rules

1. Breaking changes are allowed when the active specification explicitly requires them; update all consumers in the same change set.
2. Keep type defaults deterministic and complete.
3. Capture non-obvious schema rationale with succinct inline comments.

## Validation

- Add or update focused tests in `../Grace.Types.Tests` for changed type behavior.
- Run `dotnet build --configuration Release` and affected test projects.
