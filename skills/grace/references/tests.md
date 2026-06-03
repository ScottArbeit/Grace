# Grace Tests

Load this reference when adding, updating, repairing, or selecting tests.

## Test Project Map

| Behavior | Preferred Tests |
| -------- | --------------- |
| `Grace.Types` contracts | `src/Grace.Types.Tests` |
| Authorization, endpoint manifest, PAT, claims, path permissions | `src/Grace.Authorization.Tests` |
| HTTP API and server-surface actor behavior | `src/Grace.Server.Tests` |
| CLI parsing, command routing, local config/history | `src/Grace.CLI.Tests` |
| SDK client contract | SDK consumer tests or server contract tests |

## Server Integration Tests

Key guidance from `src/Grace.Server.Tests/AGENTS.md`:

- Tests boot the local Aspire stack with Cosmos, Azurite, Service Bus emulator, Redis, and Grace.Server.
- The first HTTP call in the run must be `POST /owner/create`, done by the setup fixture.
- Use the shared host module in `AspireTestHost.fs`.
- Reuse shared state such as `HttpClient`, owner ID, and Service Bus settings.
- Validate Service Bus messages through the dedicated test subscription so tests do not race the server processor.
- Do not reintroduce Dapr sidecars, Dapr ports, or Dapr cleanup logic.
- Log useful diagnostics on failure and redact secrets.

## CLI Tests

Key guidance from `src/Grace.CLI.Tests/AGENTS.md`:

- Match test files to command files when practical.
- Keep tests isolated; back up and restore files touched under `~/.grace`.
- Prefer deterministic timestamps for history-related tests.
- Use FsUnit for assertions and FsCheck when properties add value.

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

Use focused tests first. Then run the selected profile.

```powershell
dotnet test --no-build --configuration Release src/Grace.Types.Tests/Grace.Types.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj
dotnet test --no-build --configuration Release src/Grace.Server.Tests/Grace.Server.Tests.fsproj
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
```

Run `dotnet tool run fantomas --recurse .` from `./src` after F# changes when formatting is needed.
