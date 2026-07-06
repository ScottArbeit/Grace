
# Grace Tests

Load this reference when adding, updating, repairing, or selecting tests.

## Test Project Map

| Behavior | Preferred Tests |
| -------- | --------------- |
| `Grace.Types` contracts | `src/Grace.Types.Tests` |
| Authorization, endpoint manifest, PAT, claims, path permissions | `src/Grace.Authorization.Tests` |
| No-Aspire server helpers, contracts, and deterministic unit coverage | `src/Grace.Server.Unit.Tests` |
| HTTP API, Aspire resources, storage routes, and server-surface actor behavior | `src/Grace.Server.Tests` |
| CLI pure parsing, command routing, local config/history | `src/Grace.CLI.Tests` |
| SDK client contract | SDK consumer tests or server contract tests |

## Server Integration Tests

Key guidance from `src/Grace.Server.Tests/AGENTS.md`:

- Tests boot the local Aspire stack with Cosmos, Azurite, Service Bus emulator, Redis, and Grace.Server.
- Keep HTTP-hosted, emulator/resource, storage route, upload-session, and cross-service behavior here.
- The first HTTP call in the run must be `POST /owner/create`, done by the setup fixture.
- Use the shared host module in `AspireTestHost.fs`.
- Reuse shared state such as `HttpClient`, owner ID, and Service Bus settings.
- Validate Service Bus messages through the dedicated test subscription so tests do not race the server processor.
- Do not reintroduce Dapr sidecars, Dapr ports, or Dapr cleanup logic.
- Log useful diagnostics on failure and redact secrets.
- This project remains integration-controlled; do not add assembly-level parallel defaults without a new audit.

## Server Unit Tests

Key guidance from `src/Grace.Server.Unit.Tests/AGENTS.md`:

- Use this project for pure, no-Aspire server-adjacent helper, contract, determinism, and unit-shaped coverage.
- Keep it free of HTTP hosting, emulators, Service Bus, blob storage, Redis, `Grace.Server.Tests.Services`, and
  `Grace.Aspire.AppHost`.
- Move ambiguous behavior back to `Grace.Server.Tests` unless it is clearly no-Aspire and unit-shaped.
- Assembly-level NUnit defaults are deferred while process-static approval-store mutation remains in the project.

## CLI Tests

Key guidance from `src/Grace.CLI.Tests/AGENTS.md`:

- Match test files to command files when practical.
- Keep pure parse-only tests in `*.CLI.Parsing.Tests.fs` files so they can be parallelized.
- Keep invocation, config, environment, console, filesystem, current-directory, local-state, and SDK identity tests in
  serialized non-parsing files.
- Keep tests isolated; back up and restore files touched under `~/.grace`.
- Prefer deterministic timestamps for history-related tests.
- Use FsUnit for assertions and FsCheck when properties add value.
- Assembly-level NUnit defaults are deferred while global/current-process mutations remain in the assembly.

## Type Tests

Key guidance from `src/Grace.Types.Tests/AGENTS.md`:

- Match tests to source type files when practical.
- Keep tests deterministic with fixed timestamps and explicit GUIDs.
- Test public contract semantics instead of implementation internals.
- Avoid server or Aspire dependencies.

## Regression Targets

Add canned regressions for:

- replay and idempotency
- duplicate client decision IDs
- stale IDs and cross-scope object IDs
- list filtering and stored-object authorization after load
- mutable configuration or rule/policy snapshots
- retry and cancellation behavior
- HTTP response or stream disposal
- secret, token, URL, and user-payload redaction
- Service Bus readiness and message ordering
- manifest reuse range presence and stale metadata versions
- FS3511 hazards from recursion or `for ... in` loops inside `task { }`

## Validation Commands

Use focused tests first. Then run the selected profile. Build the matching project in Release before any
project-specific `dotnet test --no-build` command.

```powershell
dotnet build --configuration Release src/Grace.Types.Tests/Grace.Types.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.Types.Tests/Grace.Types.Tests.fsproj
dotnet build --configuration Release src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj
dotnet build --configuration Release src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj
dotnet build --configuration Release src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj
dotnet build --configuration Release src/Grace.Server.Tests/Grace.Server.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.Server.Tests/Grace.Server.Tests.fsproj
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
```

`validate.ps1` uses one solution-level `dotnet test "src/Grace.slnx"` invocation with Fast or Full selection filters,
not custom per-project process fan-out. Fast selects Authorization, CLI, Types, and Server.Unit tests. Full adds Server
integration tests.

Run `dotnet tool run fantomas --recurse .` from `./src` after F# changes when formatting is needed.

## Review-Resistant Proof Matrix

For each behavior-changing issue, select focused tests from this matrix:

- positive success through the closest stable seam
- negative denial, unsupported value, malformed input, hidden/missing resource, duplicate, and cross-scope cases
- regression for known prior review findings
- boundary cases for identity, ordering, limits, and collisions
- replay/retry/terminal-state behavior after partial success or cleanup
- stale-authority cases where state changes between decision and mutation/materialization/publication
- contract propagation tests for CLI/SDK/OpenAPI/generated artifacts when public surfaces change
- no-oracle tests for hidden or unauthorized resources when absence/non-observability is the requirement

A test that only executes a path is not enough. The assertion must fail if the unsafe behavior returns.
