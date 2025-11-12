# Grace.Server Local Development with .NET Aspire

## Prerequisites

1. **.NET 10 SDK** – Install the preview SDK from <https://dotnet.microsoft.com/download>.
2. **Docker Desktop** – Enable Docker before starting Aspire:
   - Windows/macOS: <https://www.docker.com/products/docker-desktop>
   - Linux: install `docker.io` (Ubuntu) or the equivalent package for your distro.
3. **Resources** – Reserve at least 8 GB RAM and 10 GB free disk space. The Cosmos DB emulator and SQL Server container for Service Bus both consume several GB of memory.
4. **Cosmos certificates** – With non-SSL ports enabled the emulator uses HTTP. If you prefer HTTPS, export and trust the certificate as described in [Azure Cosmos DB emulator for Linux](https://learn.microsoft.com/azure/cosmos-db/emulator-linux).

## Start Aspire

> The Aspire app host lives in `Grace.Aspire.AppHost`. It orchestrates Azurite, Cosmos DB, Redis, SQL Server, the Service Bus emulator, and `Grace.Server` itself.

### Visual Studio

1. Set `Grace.Aspire.AppHost` as the startup project.
2. Press **F5**. Visual Studio launches the Aspire host and opens the dashboard at `http://localhost:18888`.

### .NET CLI

```bash
cd Grace.Aspire.AppHost
DOTNET_ENVIRONMENT=Development dotnet run
```

The CLI host prints connection details for each emulator (Azurite, Redis, Cosmos, Service Bus) as they start.

## Verify Components

Open `http://localhost:18888` and confirm the following resources show `Running`:

- `azurite` – Azure Storage emulator (blob/queue/table) on ports `10000-10002`
- `redis` – Redis cache on port `6379`
- `cosmos-emulator` – Cosmos DB emulator on port `8081`
- `servicebus-sql` – SQL Server container required by the Service Bus emulator
- `service-bus-emulator` – Service Bus emulator (AMQP on `5672`, management UI on `9200`)
- `grace-server` – HTTP `5000` / HTTPS `5001`

## Smoke Tests

1. **Health check**
   ```bash
   curl http://localhost:5000/healthz
   ```
   Expected output: `Grace server seems healthy!`
2. **Traces & metrics** – In the Aspire dashboard, select **grace-server** → **Traces** or **Metrics** to review OpenTelemetry data sent via the OTLP exporter.
3. **Logs** – Within the same resource view, confirm log entries for Orleans startup and Aspire instrumentation.

## Service Bus Emulator Connection String

The .NET SDK expects a connection string that ends with `UseDevelopmentEmulator=true`. After the emulator finishes booting:

1. Browse to `http://localhost:9200` (Service Bus emulator management portal).
2. Copy the `RootManageSharedAccessKey` connection string shown in the portal.
3. Before launching Aspire, set the lowercase environment variable so `Grace.Server` can connect:
   ```powershell
   $env:azureservicebusconnectionstring = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<SAS_KEY>;UseDevelopmentEmulator=true;"
   dotnet run
   ```

Environment keys in `Grace.Shared.Constants` are lowercase. [Microsoft Learn confirms configuration keys are case-insensitive](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#environment-variables-configuration-provider), so this works for Windows and Linux hosts.

## Run Tests

After the host is up:

```bash
cd ..\Grace.Server.Tests
DOTNET_ENVIRONMENT=Development dotnet test --no-build
```

Integration tests reuse the running emulators. Shut down Aspire when tests finish to release containers and ports.

## Troubleshooting

- **Port conflicts** – Update bindings inside `Grace.Aspire.AppHost/Program.cs` if ports `5000`, `5001`, `10000–10002`, `8081`, `10251–10255`, `5672`, `9200`, or `21433` are already used.
- **Cosmos DB emulator** – First launch can take several minutes. Inspect logs with `docker logs cosmos-emulator`.
- **Service Bus emulator** – The Service Bus container waits for SQL Server. If startup fails, check `docker logs servicebus-sql` for password or EULA issues.
- **Missing telemetry** – `OTLP_ENDPOINT_URL` must reach `http://localhost:18889`. In Azure, provide `APPLICATIONINSIGHTS_CONNECTION_STRING` so Azure Monitor exporters activate.

## Next Steps

- Review `infra/app-service.bicep` for a production-ready App Service deployment template.
- Set GitHub secrets (`AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`) before enabling the workflow in `.github/workflows/deploy-to-app-service.yml`.
