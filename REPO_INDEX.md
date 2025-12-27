# REPO_INDEX.md (Grace jump table)

This file is a machine-oriented index to help tools and humans quickly find the right code.

---

## Suggested search strategy for AllGrace.txt

1. Refer to AllGrace_Index.md for exact starting and ending line numbers for each file.
2. Use the entry points list below to decide where to navigate to.
3. Jump to the exact starting line of the file, and search within the starting and ending line numbers.

## Top entry points (open these first)

### Grace Server (HTTP + DI + Orleans wiring)

- `src/Grace.Server/Startup.Server.fs`
  HTTP routes/endpoints and server composition entry points.
- `src/Grace.Server/Program.Server.fs`
  Host startup (Kestrel/Orleans host build/run).

### Orleans grains (domain behavior)

- `src/Grace.Actors/**/*.fs`
  Grain/actor implementations. Look for `*Actor.fs` as primary behavior files.

### Domain types, events, DTOs

- `src/Grace.Types/**/*.fs`
  Discriminated unions, events, DTOs, identifiers, serialization shapes.

### Local orchestration (emulators/containers)

- `src/Grace.Aspire.AppHost/Program.Aspire.AppHost.cs`
  Local dev topology: Cosmos emulator, Azurite, Service Bus emulator, Redis, and Grace.Server.

### Integration tests

- `src/Grace.Server.Tests/General.Server.Tests.fs`
  Test harness bootstrapping and shared test state.
- `src/Grace.Server.Tests/Owner.Server.Tests.fs`
  Owner API tests.
- `src/Grace.Server.Tests/Repository.Server.Tests.fs`
  Repository API tests.

---

## Cross-cutting �where is X implemented?�

### JSON serialization settings

- Search: `JsonSerializerOptions`
- Likely in: `src/Grace.Shared/**` or `src/Grace.Server/**`

### Service Bus publishing

- Search: `publishGraceEvent`, `ServiceBusMessage`, `GraceEvent`
- Likely in:
  - `src/Grace.Actors/**` (where events are emitted)
  - `src/Grace.Server/**` (wiring/config)
  - `src/Grace.Shared/**` (helpers)

### Cosmos DB / persistence wiring

- Search: `AddCosmosGrainStorage`, `CosmosClient`, `UseAzureStorageClustering`
- Likely in: `src/Grace.Server/Startup.Server.fs`

### Azure Blob grain storage

- Search: `AddAzureBlobGrainStorage`, `BlobServiceClient`
- Likely in: `src/Grace.Server/Startup.Server.fs`

### CLI commands

- Search: `grace owner create`, `Command`, `System.CommandLine`
- Likely in: `src/Grace.CLI/**`

---

## Local development �source of truth�

- Aspire AppHost defines the runnable local environment. If there is a discrepancy between older docs/tests and AppHost, prefer AppHost.

---

## Obsolete / legacy systems

- Dapr is not used anymore. Any Dapr references in tests or tooling are legacy and should be removed or ignored.
