# Grace.Server.Tests

Integration tests that boot the local Aspire stack (Cosmos, Azurite, Service Bus emulator, Redis, Grace.Server) and
exercise real HTTP flows. Pure, no-Aspire coverage belongs in `Grace.Server.Unit.Tests`.

- Match files to server files: `Grace.Server/Queue.Server.fs` -> `Queue.Server.Tests.fs`. Keep auth tests separate.
- Hosting goes through `AspireTestHost.fs`. The first HTTP call of the run must be `POST /owner/create`; the
  `[<SetUpFixture>]` does it and sets up shared state (HttpClient, OwnerId, Service Bus settings).
- Read Service Bus messages from the test subscription (`${serverSubscription}-tests`) so tests don't race the server's
  processor.
- A test that needs Grace.Server stopped or restarted must use `SharedGraceServerRestartScenarios`, not its own
  stop/start. Prepare repository contexts while the server runs, make offline edits in the shared stopped-server
  callback, and expose post-restart checks as named tests with no ordering dependencies. Keep explicit accounting
  measurements and Redis restart experiments separate.
- Don't reintroduce Dapr sidecars, ports, or cleanup logic.
- Log the server base address and Service Bus settings on failure, with secrets redacted. `GRACE_TEST_CLEANUP=1`
  controls resource cleanup.
- Run integration tests locally with `pwsh ./scripts/validate.ps1 -Full` or a focused `dotnet test` filter.
