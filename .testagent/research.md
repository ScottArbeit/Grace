# Issue #1038 Libraries test research

## Scope

Issue #1038 adds the complete remote Libraries contract. DEC-039 through DEC-043 replace the candidate's rejected shared Cosmos key hierarchy and old public vocabulary without changing the accepted canonical-change-first algorithm. Tests must cover Library records and validation, the repository Library catalog, purpose-specific persistence routing and bounds, the one-way `RepositoryLibraryActor`, authorization, HTTP routes, immutable byte access, SDK, Library catalog CLI, generated contracts, and version-control path exclusion.

## Existing conventions

- `Grace.Types.Tests` uses NUnit fixtures for deterministic public-contract behavior.
- `Grace.Authorization.Tests` and authorization-specific server tests own operation, role, route-manifest, and hidden-resource behavior.
- `Grace.Server.Unit.Tests` owns pure server helpers and deterministic persistence decisions that do not require Aspire.
- `Grace.Server.Tests` owns HTTP, Cosmos Emulator, immutable storage, and restart behavior through the hosted server.
- `Grace.CLI.Tests` owns parser, command invocation, JSON output, and inert schema/example behavior.
- Focused Release builds precede `dotnet test --no-build`.

## Acceptance checklist

- Exact Library identity, enum, DTO, outcome, parameter, catalog, change-page, and path-normalization contracts.
- Persisted initial empty Library catalog plus exact-version add/remove and rejection behavior.
- `LibraryRead` and `LibraryWrite` role composition and no-oracle checks.
- One deterministic accepted change, stale/conflict/rejection outcomes, operation replay, identity mismatch, and projection repair after restart.
- Purpose-specific container keys, exact-partition routing, prohibited cross-partition queries, and rejection before item-head or namespace-slot document 100,001.
- One-way repository serialization through `RepositoryLibraryActor`, including catalog/change and Save/Reference/Branch interleavings without high-cardinality actor state.
- Current item and slot reads, ordered change pages, bootstrap pages, receipt reads, truthful status, and immutable byte grants.
- Every public `/libraries` route and `Grace.SDK.Libraries` method, with unsupported local/deferred routes absent.
- Library catalog CLI list/get/add/remove behavior and inert schema/examples.
- Static OpenAPI, TypeScript/Python/Rust generation, and .NET facade metadata remain current.
- Configured Libraries are excluded from Save and Branch/WDU version-control effects at the exact Library catalog version.
- Current source, static contracts, generated clients, tests, and docs contain no stale Library-owned synchronized, change, root-object, or delta vocabulary.
- Documentation states the remote-only boundary and keeps Issue #1039 behavior absent.

## Sensitive behavior

Tests must prove that local participation, local SQLite, filesystem publication, a fourth WDU caller, Cache, Diff, Work Item Attachment changes, cross-partition queries, TTL or cleanup lifecycle, compatibility aliases, and internal Cosmos or grant details remain absent.
