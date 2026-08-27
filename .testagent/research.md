# Issue #1038 test research

## Scope

Issue #1038 adds the complete remote Synchronized Content contract. Tests must cover the new domain records and validation, repository root configuration, authorization, bounded server persistence and recovery, HTTP routes, immutable byte access, SDK, root CLI, generated contracts, and version-control path exclusion.

## Existing conventions

- `Grace.Types.Tests` uses NUnit fixtures for deterministic public-contract behavior.
- `Grace.Authorization.Tests` and authorization-specific server tests own operation, role, route-manifest, and hidden-resource behavior.
- `Grace.Server.Unit.Tests` owns pure server helpers and deterministic persistence decisions that do not require Aspire.
- `Grace.Server.Tests` owns HTTP, Cosmos Emulator, immutable storage, and restart behavior through the hosted server.
- `Grace.CLI.Tests` owns parser, command invocation, JSON output, and inert schema/example behavior.
- Focused Release builds precede `dotnet test --no-build`.

## Acceptance checklist

- Exact synchronized identity, enum, DTO, outcome, parameter, and path-normalization contracts.
- Persisted initial empty root configuration plus exact-version add/remove and rejection behavior.
- `SynchronizedContentRead` and `SynchronizedContentWrite` role composition and no-oracle checks.
- One deterministic accepted mutation, stale/conflict/rejection outcomes, operation replay, identity mismatch, and projection repair after restart.
- Current item and slot reads, ordered deltas, bootstrap pages, receipt reads, truthful status, and immutable byte grants.
- Every public `/sync` route and SDK method, with unsupported local/deferred routes absent.
- Root CLI list/get/add/remove behavior and inert schema/examples.
- Static OpenAPI, TypeScript/Python/Rust generation, and .NET facade metadata remain current.
- Configured roots are excluded from Save and Branch/WDU version-control effects at the exact root-policy version.
- Documentation states the remote-only boundary and keeps Issue #1039 behavior absent.

## Sensitive behavior

Tests must prove that local participation, local SQLite, filesystem publication, a fourth WDU caller, Cache, Diff, Work Item Attachment changes, and internal Cosmos or grant details remain absent.
