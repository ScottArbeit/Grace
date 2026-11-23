# Grace.Server.Unit.Tests

Unit, contract, and pure helper tests for server-side code that does not need Aspire. This project is part of the Fast
validation set.

- No HTTP-hosted calls, emulators, Service Bus, blob storage, Redis, `Grace.Server.Tests.Services`, `AspireTestHost.fs`,
  or references to `Grace.Aspire.AppHost`. If it's unclear whether a test needs those, put it in `Grace.Server.Tests`.
- Some files keep historical `Grace.Server.Tests` namespaces; the Fast filter selects this project by its compile items,
  so keep tests listed in the project file.
