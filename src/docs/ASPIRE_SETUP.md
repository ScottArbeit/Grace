# Grace.Server Local Development with .NET Aspire

## Prerequisites

1. **.NET 10 SDK** – Install the SDK pinned in `global.json` (or compatible
   roll-forward).
2. **Docker Desktop** – Enable Docker before starting Aspire:
   - Windows/macOS: <https://www.docker.com/products/docker-desktop>
   - Linux: install `docker.io` (Ubuntu) or the equivalent package for your
     distro.
3. **Resources** – Reserve at least 8 GB RAM and 10 GB free disk space. The
   Cosmos DB emulator and SQL Server container for Service Bus both consume
   several GB of memory.
4. **Cosmos certificates** – With non-SSL ports enabled the emulator uses HTTP.
   If you prefer HTTPS, export and trust the certificate as described in
   [Azure Cosmos DB emulator for Linux](https://learn.microsoft.com/azure/cosmos-db/emulator-linux).

## Preferred Local Loop

Use the agent-ready scripts as the canonical entrypoint:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
```

Run `pwsh ./scripts/validate.ps1 -Full` when you need the Aspire integration
coverage from `Grace.Server.Tests`.

## Environment Variables

For the full environment variable inventory, `dotnet user-secrets` guidance,
and profile-specific requirements (`DebugLocal` vs `DebugAzure`), use
`src/docs/ENVIRONMENT.md` as the canonical reference.

## Start Aspire

> The Aspire app host lives in `Grace.Aspire.AppHost`. It orchestrates Azurite,
> Cosmos DB, Redis, SQL Server, the Service Bus emulator, and `Grace.Server`
> itself.

### Visual Studio

1. Set `Grace.Aspire.AppHost` as the startup project.
2. Press **F5**. Visual Studio launches the Aspire host and opens the dashboard
   at `http://localhost:18888`.

### .NET CLI

```bash
cd Grace.Aspire.AppHost
DOTNET_ENVIRONMENT=Development dotnet run
```

The CLI host prints connection details for each emulator (Azurite, Redis,
Cosmos, Service Bus) as they start.

## Verify Components

Open `http://localhost:18888` and confirm the following resources show
`Running`:

- `azurite` – Azure Storage emulator (blob/queue/table) on ports `10000-10002`
- `redis` – Redis cache on port `6379`
- `cosmos` – Cosmos DB emulator on port `8081`
- `servicebus-sql` – SQL Server container required by the Service Bus emulator
  and used locally for the `GraceOperations` SQL database
- `servicebus-emulator` – Service Bus emulator (AMQP on `5672`, management
  endpoint on `5300`)
- `grace-server` – HTTP `5000` / HTTPS `5001`
- `grace-operations-worker` – operational usage fact ingestion worker for the
  `grace-operational-facts` topic and durable `operational-facts-processor`
  subscription

Redis is provisioned by AppHost and its host/port are forwarded to
`Grace.Server`. Current startup code does not enable Redis-backed SignalR, so
Redis remains an explicit AppHost dependency pending a follow-up runtime
decision rather than a prerequisite proven by the integration tests.

The local operations worker creates the `ops` SQL schema and usage fact tables
inside a `GraceOperations` database on the local SQL Server container before it
starts the Service Bus processor. It completes a Service Bus message only after
the operations data layer accepts the raw fact or reports that the fact was
already processed.

## Smoke Tests

1. **Health check**

   ```bash
   curl http://localhost:5000/healthz
   ```

   Expected output: `Grace server seems healthy!`
2. **Traces & metrics** – In the Aspire dashboard, select **grace-server** →
   **Traces** or **Metrics** to review OpenTelemetry data sent via the OTLP
   exporter.
3. **Logs** – Within the same resource view, confirm log entries for Orleans
   startup and Aspire instrumentation.
4. **Operations worker** – In the `grace-operations-worker` logs, confirm the
   startup line for topic `grace-operational-facts` and subscription
   `operational-facts-processor`.

## Service Bus Emulator Connection String

The .NET SDK expects a connection string that ends with
`UseDevelopmentEmulator=true`. AppHost exposes the Service Bus emulator AMQP
endpoint on `localhost:5672` and the management endpoint on `localhost:5300`;
the management endpoint is not a portal that shows keys.

When AppHost launches `grace-server`, it provides the local emulator connection
string automatically with `SharedAccessKey=SAS_KEY_VALUE`. To launch
`Grace.Server` manually against the local emulator, set the lowercase
environment variable before starting the server:

```powershell
$env:azureservicebusconnectionstring =
  "Endpoint=sb://localhost/;" +
  "SharedAccessKeyName=RootManageSharedAccessKey;" +
  "SharedAccessKey=SAS_KEY_VALUE;" +
  "UseDevelopmentEmulator=true;"

dotnet run
```

Environment keys in `Grace.Shared.Constants` are lowercase. Microsoft Learn
confirms configuration keys are case-insensitive, so this works for Windows and
Linux hosts.

## Run Tests

Use the repository validation script for the Aspire-backed integration suite:

```powershell
pwsh ./scripts/validate.ps1 -Full
```

`Grace.Server.Tests` starts its own Aspire host, then proves storage, Cosmos DB,
and Service Bus readiness before running server integration tests. The
`GRACE_TEST_SKIP_SERVICEBUS=1` environment variable is not a supported
`Grace.Server.Tests` profile today because the shared setup drains the Service
Bus test subscription and verifies the Owner Created event.

### Operations Usage Tracer Bullet

`Grace.Server.Tests` includes a dev/test-only operations tracer bullet that
publishes one repository-storage `UsageFact` to the dedicated operational facts
topic, waits for `grace-operations-worker` to consume it, and queries the local
`GraceOperations` SQL database for the raw fact and UTC minute aggregate. The
test also injects a duplicate delivery with the same `UsageFactId` and verifies
that the raw row and aggregate quantity remain single-counted.

The tracer bullet is proof of the local operational usage pipeline only:

- It uses the existing Aspire-local Service Bus emulator and SQL Server
  container.
- It keeps `UsageFactId` and `CorrelationId` distinct in the end-to-end
  evidence.
- It verifies malformed or unsupported usage payloads are dead-lettered and do
  not become authoritative SQL usage.
- It does not add customer-facing usage APIs, pricing, charge ledgers, billing
  close, invoices, dashboards, Azure Cost Management ingestion, Azure Storage
  diagnostic ingestion, or a durable outbox.

## Troubleshooting

- **Port conflicts** – Update bindings inside
  `Grace.Aspire.AppHost/Program.Aspire.AppHost.fs` if ports `5000`, `5001`,
  `10000–10002`, `8081`, `10251–10255`, `5672`, `5300`, or `21433` are already
  used.
- **Cosmos DB emulator** – First launch can take several minutes. Inspect logs
  with `docker logs cosmosdb-emulator`, or `docker logs cosmosdb-emulator-<suffix>` for suffixed test runs.
- **Service Bus emulator** – The Service Bus container waits for SQL Server. If
  startup fails, check `docker logs servicebus-sql` for password or EULA issues.
- **Missing telemetry** – `OTLP_ENDPOINT_URL` must reach
  `http://localhost:18889`. In Azure, provide
  `APPLICATIONINSIGHTS_CONNECTION_STRING` so Azure Monitor exporters activate.

## Next Steps

- Review `infra/app-service.bicep` for a production-ready App Service
  deployment template.
- Set GitHub secrets (`AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`,
  `AZURE_CLIENT_ID`) before enabling the workflow in
  `.github/workflows/deploy-to-app-service.yml`.
