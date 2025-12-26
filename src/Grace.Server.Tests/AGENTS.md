# Grace.Server.Tests Agents Guide

Global policies live in `../AGENTS.md`; follow them before touching tests here.

## Purpose
- End-to-end integration tests that boot the local Aspire stack (Cosmos + Azurite + Service Bus emulator + Redis + Grace.Server).
- Verify real HTTP flows against `Grace.Server` with an initialized Service Bus test subscription.

## Key Patterns
- Tests use Aspire hosting via `Aspire.Hosting.Testing` and the shared host module in `AspireTestHost.fs`.
- The first HTTP call in the test run must be `POST /owner/create` (done in the `[<SetUpFixture>]`).
- Service Bus validation must read from the **test subscription** (`${serverSubscription}-tests`) to avoid racing the server processor.
- Shared state (HttpClient, OwnerId, Service Bus settings) is set during `OneTimeSetUp` and reused by test modules.

## Notes
- Do **not** reintroduce Dapr sidecars, Dapr ports, or Dapr cleanup logic.
- Keep diagnostics helpful: log server base address and Service Bus settings (redact secrets).
- Set environment variable `GRACE_TEST_CLEANUP=1` to control resource cleanup after tests.

## Validation
- Run `dotnet build --configuration Release`.
- Run `dotnet test --no-build --configuration Release`.
- Run `fantomas .` after F# changes.
