# Grace.Server.Tests

Integration tests that boot the local Aspire stack (Cosmos, Azurite, Service Bus emulator, Redis, Grace.Server) and
exercise real HTTP flows. Pure, no-Aspire coverage belongs in `Grace.Server.Unit.Tests`.

- Hosting goes through `AspireTestHost.fs`. The first HTTP call of the run must be `POST /owner/create`; the
  `[<SetUpFixture>]` does it and sets up shared state (HttpClient, OwnerId, Service Bus settings).
- Read Service Bus messages from the test subscription (`<subscription>-tests`) so tests don't race the server's
  processor.
- A test that needs Grace.Server stopped or restarted must use `SharedGraceServerRestartScenarios`, not its own
  stop/start. Prepare repository contexts while the server runs, make offline edits in the shared stopped-server
  callback, and expose post-restart checks as named tests with no ordering dependencies. Keep explicit accounting
  measurements and Redis restart experiments separate.
- Log the server base address and Service Bus settings on failure, with secrets redacted.
