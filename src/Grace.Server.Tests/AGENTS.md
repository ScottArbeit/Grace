# Grace.Server.Tests Agents Guide

Global policies live in `../AGENTS.md`; follow them before touching tests here.

## Purpose

- End-to-end integration tests that boot the local Aspire stack (Cosmos + Azurite + Service Bus emulator + Redis + Grace.Server).
- Verify real HTTP flows against `Grace.Server` with an initialized Service Bus test subscription.
- Validate actor behavior through server endpoints/contracts wherever possible.
- Keep this project for HTTP-hosted, emulator-backed, resource, storage route, upload-session, and cross-service
  behavior. Move pure no-Aspire helper, contract, or deterministic coverage to `Grace.Server.Unit.Tests`.

## Test File Organization

- Match server test files to server implementation files when practical:
  - `Grace.Server/Access.Server.fs` -> `Grace.Server.Tests/Access.Server.Tests.fs`
  - `Grace.Server/Eventing.Server.fs` -> `Grace.Server.Tests/Eventing.Server.Tests.fs`
  - `Grace.Server/Notification.Server.fs` -> `Grace.Server.Tests/Notification.Server.Tests.fs`
  - `Grace.Server/Queue.Server.fs` -> `Grace.Server.Tests/Queue.Server.Tests.fs`
  - `Grace.Server/WorkItem.Server.fs` -> `Grace.Server.Tests/WorkItem.Server.Tests.fs`
- Keep auth-focused tests separate for now.
- Do not add pure SDK-contract or helper-only fixtures here when they can run without Aspire. Prefer
  `Grace.Server.Unit.Tests` for those tests.

## Key Patterns

- Tests use Aspire hosting via `Aspire.Hosting.Testing` and the shared host module in `AspireTestHost.fs`.
- The first HTTP call in the test run must be `POST /owner/create` (done in the `[<SetUpFixture>]`).
- Service Bus validation must read from the test subscription (`${serverSubscription}-tests`) to avoid racing the
  server processor.
- Shared state (HttpClient, OwnerId, Service Bus settings) is set during `OneTimeSetUp` and reused by test modules.
- This project remains integration-controlled. Do not add assembly-level parallel defaults unless a future issue proves
  the shared Aspire resources and setup state are safe under that change.

## Notes

- Do not reintroduce Dapr sidecars, Dapr ports, or Dapr cleanup logic.
- Keep diagnostics helpful: log server base address and Service Bus settings (redact secrets).
- Set environment variable `GRACE_TEST_CLEANUP=1` to control resource cleanup after tests.

## Validation

- Run the matching Release build before any project-specific `dotnet test --no-build` command.
- Run `dotnet test --configuration Release --no-build src/Grace.Server.Tests/Grace.Server.Tests.fsproj` only after that
  build context exists.
- Run `pwsh ./scripts/validate.ps1 -Full` for the integration gate. The script uses one solution-level `dotnet test`
  invocation with filters rather than custom per-project process fan-out.
- Run `dotnet tool run fantomas --recurse .` from `./src` after F# changes.
