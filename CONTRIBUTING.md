# Contributing to Grace Version Control System

Thanks for considering a contribution to **Grace Version Control System**.

This repo is primarily **F#** and targets **.NET 10**.

## Quick start

1. Fork the repo and create your branch in your fork.
2. Run the prerequisite check:

   - From the repo root:
     - `pwsh ./scripts/bootstrap.ps1`
       - Optional: `-SkipDocker` if you don’t have Docker available yet.

3. Build:

   - `dotnet build ./src/Grace.sln`

4. Test:

   - `dotnet test ./src/Grace.sln` (optionally add `-c Release`)
   - Or run the repo validator: `pwsh ./scripts/validate.ps1 -Full`

5. Format F#:

   - From `./src`: `dotnet tool run fantomas --recurse .`

## Contribution workflow

- Use the **GitHub fork + pull request** workflow.
- Keep PRs focused (one change per PR whenever practical).
- Add or update tests when changing behavior.
- Please add any useful AI prompts you used for diagnosis or implementation to the PR description.

## Prerequisites

- **.NET 10 SDK** (see: https://dotnet.microsoft.com/download)
- **PowerShell 7+** (see: https://learn.microsoft.com/powershell/)
- **Docker Desktop** or **Podman** (recommended for local emulators / Aspire DebugLocal)
  - Windows/macOS: https://www.docker.com/products/docker-desktop/
  - Linux: use Docker Engine for your distro

`pwsh ./scripts/bootstrap.ps1` is the recommended sanity check. It verifies tools and performs `dotnet tool restore` + `dotnet restore`.

## Build

From the repo root:

- `dotnet build ./src/Grace.sln`

## Tests

From the repo root:

- `dotnet test ./src/Grace.sln` (optionally `-c Release`)

This repo also includes a validation script:

- Fast loop: `pwsh ./scripts/validate.ps1 -Fast`
- Full validation (includes Aspire integration coverage): `pwsh ./scripts/validate.ps1 -Full`

## Formatting (required)

F# is formatted with **Fantomas**.

From `./src`:

- Apply formatting: `dotnet tool run fantomas --recurse .`

If you’re proposing CI changes, CI should enforce formatting checks (if it doesn’t already).

Fantomas:

- https://github.com/fsprojects/fantomas

## Running Grace locally (Aspire)

Grace can be run locally using **Docker containers and emulators**, via the Aspire AppHost launch configuration:

- `DebugLocal`: local containers/emulators (e.g., Azurite, Cosmos emulator, Service Bus emulator)
- `DebugAzure`: runs locally but expects real Azure resources (Cosmos DB, Blob Storage, Service Bus, etc.)

Where to look in the repo:

- `Grace.Aspire.AppHost/Properties/launchSettings.json`
- `Grace.Aspire.AppHost/Program.Aspire.AppHost.cs`

Aspire:

- https://learn.microsoft.com/dotnet/aspire/

## Configuration and secrets

Grace supports multiple configuration sources (depending on your setup):

- Environment variables
- `.NET user-secrets` (recommended for local dev)
- `appsettings*.json` (project-specific)
- Aspire launch profiles (`DebugLocal` / `DebugAzure`)
- Azure resource configuration (when using `DebugAzure`)

### Using .NET user-secrets (recommended)

The simplest developer setup is to store secrets in **user-secrets** for the `Grace.Server` project.

User-secrets documentation:

- https://learn.microsoft.com/aspnet/core/security/app-secrets

Examples (run from the repo root):

- List secrets:
  - `dotnet user-secrets list --project ./src/Grace.Server/Grace.Server.fsproj`

- Set a secret:
  - `dotnet user-secrets set --project ./src/Grace.Server/Grace.Server.fsproj "grace__azurecosmosdb__connectionstring" "<value>"`

- Remove a secret:
  - `dotnet user-secrets remove --project ./src/Grace.Server/Grace.Server.fsproj "grace__azurecosmosdb__connectionstring"`

Notes:

- Keys frequently use `__` to map to hierarchical configuration (see: https://learn.microsoft.com/aspnet/core/fundamentals/configuration/)
- The Aspire AppHost can read the server user-secrets id and forward selected auth settings (see `Grace.Aspire.AppHost/AGENTS.md`).

## Environment variables

The canonical list of environment variables is defined in `Grace.Shared/Constants.Shared.fs` under `Constants.EnvironmentVariables`.

In general, values may come from:

- your shell environment,
- Aspire launch profile environment,
- `.NET user-secrets` for `Grace.Server`,
- or Azure resources (when using `DebugAzure`).

### Client variables

- `GRACE_SERVER_URI`
  - Grace server base URI.
  - Must include port.
  - Must not include a trailing slash.
  - Example: `http://localhost:5000`

- `GRACE_TOKEN`
  - Personal access token for non-interactive auth.

- `GRACE_TOKEN_FILE`
  - Overrides the local token file path.

### Telemetry

- `grace__applicationinsightsconnectionstring`
  - Application Insights connection string.
  - Source: user-secrets/env/Azure App Insights resource.

### Azure Cosmos DB

- `grace__azurecosmosdb__connectionstring`
  - Cosmos DB connection string.
  - Source: user-secrets/env/Azure Cosmos DB.

- `grace__azurecosmosdb__endpoint`
  - Cosmos DB endpoint for managed identity scenarios.
  - Source: Azure Cosmos DB account endpoint.

- `grace__azurecosmosdb__database_name`
  - Cosmos database name.

- `grace__azurecosmosdb__container_name`
  - Cosmos container name.

### Azure Storage (Blob)

- `grace__azure_storage__connectionstring`
  - Storage connection string.

- `grace__azure_storage__account_name`
  - Overrides storage account name (managed identity).

- `grace__azure_storage__endpoint_suffix`
  - Storage endpoint suffix (default: `core.windows.net`).

- `grace__azure_storage__key`
  - Storage account key.

- `grace__azure_storage__directoryversion_container_name`
  - Container name for DirectoryVersions.

- `grace__azure_storage__diff_container_name`
  - Container name for cached diffs.

- `grace__azure_storage__zipfile_container_name`
  - Container name for zip files.

### Azure Service Bus

- `grace__azure_service_bus__connectionstring`
  - Service Bus connection string.

- `grace__azure_service_bus__namespace`
  - Fully qualified namespace (for example `sb://<name>.servicebus.windows.net`).

- `grace__azure_service_bus__topic`
  - Topic name for Grace events.

- `grace__azure_service_bus__subscription`
  - Subscription name for Grace events.

### Pub/Sub provider selection

- `grace__pubsub__system`
  - Selects the pub-sub provider implementation.

### Orleans

- `grace__orleans__clusterid`
  - Orleans cluster id.

- `grace__orleans__serviceid`
  - Orleans service id.

Orleans:
- https://learn.microsoft.com/dotnet/orleans/

### Redis

- `grace__redis__host`
  - Redis host.

- `grace__redis__port`
  - Redis port.

Redis:
- https://redis.io/docs/latest/

### Auth (OIDC)

These are used for external auth providers such as Auth0 / OIDC.

- `grace__auth__oidc__authority`
- `grace__auth__oidc__audience`
- `grace__auth__oidc__cli_client_id`
- `grace__auth__oidc__cli_redirect_port`
- `grace__auth__oidc__cli_scopes`
- `grace__auth__oidc__m2m_client_id`
- `grace__auth__oidc__m2m_client_secret`
- `grace__auth__oidc__m2m_scopes`

OpenID Connect:
- https://openid.net/developers/how-connect-works/

### Auth (deprecated Microsoft auth)

These constants are marked deprecated in code:

- `grace__auth__microsoft__client_id`
- `grace__auth__microsoft__client_secret`
- `grace__auth__microsoft__tenant_id`
- `grace__auth__microsoft__authority`
- `grace__auth__microsoft__api_scope`
- `grace__auth__microsoft__cli_client_id`

### PAT policy

- `grace__auth__pat__default_lifetime_days`
- `grace__auth__pat__max_lifetime_days`
- `grace__auth__pat__allow_no_expiry`

### Authorization bootstrap

- `grace__authz__bootstrap__system_admin_users`
  - Semicolon-delimited list of user principals to bootstrap as SystemAdmin.

- `grace__authz__bootstrap__system_admin_groups`
  - Semicolon-delimited list of group principals to bootstrap as SystemAdmin.

### Reminders and metrics

- `grace__reminder__batch__size`
  - Batch size for reminder retrieval/publish.

- `grace__metrics__allow_anonymous`
  - Allows anonymous access to Prometheus scraping endpoint.

Prometheus:

- https://prometheus.io/docs/introduction/overview/

### Other providers (placeholders / future)

- `grace__aws_sqs__queue_url`
- `grace__aws_sqs__region`
- `grace__gcp__projectid`
- `grace__gcp__topic`
- `grace__gcp__subscription`

### Debugging/logging

- `grace__debug_environment`
  - Debug environment flag.

- `grace__log_directory`
  - Directory for Grace Server log files.

## Pull request checklist

- [ ] Builds: `dotnet build ./src/Grace.sln`
- [ ] Tests: `dotnet test ./src/Grace.sln`
- [ ] Formatting: run `dotnet tool run fantomas --recurse .` from `./src`
- [ ] Documentation updated (if behavior changed)
