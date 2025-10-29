# Grace.Actors Agents Guide

Start with `../AGENTS.md` for global rules before working on Orleans code.

## Purpose
- Define Orleans grain interfaces and implementations that orchestrate stateful workflows in Grace.
- Persist state through domain events and records defined in `Grace.Types`.

## Key Patterns
- Follow standard Orleans activation patterns; keep grain constructors light and rely on dependency injection.
- Drive state changes through explicit events or commands; keep transitions deterministic and idempotent.
- Use domain types from `Grace.Types` directly to avoid duplication and guarantee serialization compatibility.
- Separate orchestration (grains) from external service or SDK calls by delegating to helper modules/services.

## Project Rules
1. When adding new grain types or serializer changes, ensure `Grace.Orleans.CodeGen` stays in sync and regenerates as needed.
2. Keep grain state mutations safe for retries—idempotent transitions make distributed recovery predictable.
3. Document non-obvious activation, reminder, or timer behavior here so future agents can reason without scanning the entire implementation.

## Validation
- Add activation and idempotency tests for new grains or state transitions.
- Run `dotnet test --no-build` focusing on actor-related fixtures, then smoke `dotnet build --configuration Release` for the solution.
- Confirm code generation outputs (if applicable) before finalizing a PR.
