# Grace.Types.Tests Agents Guide

Read `../AGENTS.md` for repo-wide expectations before editing tests here.

## Purpose

- Unit tests for domain contracts in `Grace.Types`.
- Validate deterministic DTO/event behavior, parser contracts, and pure type-level invariants.

## Test File Organization

- Match tests to type source files when practical:
  - `Grace.Types/PromotionSet.Types.fs` -> `Grace.Types.Tests/PromotionSet.Types.Tests.fs` and `Grace.Types.Tests/PromotionSet.ConflictModel.Types.Tests.fs`
  - `Grace.Types/Validation.Types.fs` -> `Grace.Types.Tests/Validation.Types.Tests.fs`
  - `Grace.Types/Queue.Types.fs` -> `Grace.Types.Tests/Queue.Types.Tests.fs`
  - `Grace.Types/WorkItem.Types.fs` -> `Grace.Types.Tests/WorkItem.Types.Tests.fs`

## Key Patterns

1. Keep tests deterministic; use fixed timestamps and explicit GUIDs when assertions depend on exact values.
2. Test public contract semantics rather than implementation internals.
3. Avoid server or Aspire dependencies in this project.

## Validation

- Run `dotnet build --configuration Release`.
- Run `dotnet test --no-build --configuration Release src/Grace.Types.Tests/Grace.Types.Tests.fsproj`.
- Run `dotnet tool run fantomas --recurse .` from `./src` after F# changes.
