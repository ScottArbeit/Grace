# Grace Environment Inventory

This document summarizes environment dependencies and variables used by Grace.
Values are sourced from `src/Grace.Shared/Constants.Shared.fs` (module
`EnvironmentVariables`) and the Aspire host configuration in
`src/Grace.Aspire.AppHost/Program.Aspire.AppHost.cs`.

## Docker Dependencies (Local Aspire)

Local Aspire runs containers and emulators for the following dependencies:

- Azurite (Azure Storage emulator: blob/queue/table)
- Azure Cosmos DB emulator
- Azure Service Bus emulator (requires a SQL Server container)
- Redis

The Aspire dashboard is exposed on `http://localhost:18888` by default, and the
OTLP exporter is configured to send to `http://localhost:18889` unless
overridden.

## Aspire Run Modes

- `ASPIRE_RESOURCE_MODE` (not in `EnvironmentVariables`):
  - `Local` (default): uses emulators/containers for Azure dependencies.
  - `Azure`: uses real Azure resources from config/user-secrets/env vars.

## Test Toggles

- `GRACE_TESTING`: set by test host to enable test mode in Aspire.
- `GRACE_TEST_CLEANUP`: set to `1`/`true` to enable cleanup in server tests.

## Auth Forwarding in Aspire

`Grace.Aspire.AppHost` forwards the following auth-related settings into
`Grace.Server` when present (environment, user, machine, or config):

- `grace__auth__oidc__authority`
- `grace__auth__oidc__audience`
- `grace__auth__oidc__cli_client_id` (publish mode)

## Environment Variables (Canonical List)

### Telemetry

- `grace__applicationinsightsconnectionstring`: Application Insights connection
  string (optional).

### Storage (Azure)

- `grace__azure_storage__connectionstring`: Azure Storage connection string.
- `grace__azure_storage__account_name`: Storage account name override (MI).
- `grace__azure_storage__endpoint_suffix`: Storage endpoint suffix override.
- `grace__azure_storage__key`: Storage account key.
- `grace__azure_storage__directoryversion_container_name`: Directory version
  container name.
- `grace__azure_storage__diff_container_name`: Diff container name.
- `grace__azure_storage__zipfile_container_name`: Zip container name.

### Cosmos DB (Azure)

- `grace__azurecosmosdb__connectionstring`: Cosmos connection string.
- `grace__azurecosmosdb__endpoint`: Cosmos endpoint (MI).
- `grace__azurecosmosdb__database_name`: Cosmos database name.
- `grace__azurecosmosdb__container_name`: Cosmos container name.

### Service Bus (Azure)

- `grace__azure_service_bus__connectionstring`: Service Bus connection string.
- `grace__azure_service_bus__namespace`: Service Bus namespace.
- `grace__azure_service_bus__topic`: Service Bus topic name.
- `grace__azure_service_bus__subscription`: Service Bus subscription name.

### Redis

- `grace__redis__host`: Redis host.
- `grace__redis__port`: Redis port.

### Orleans

- `orleans_cluster_id`: Orleans cluster ID.
- `orleans_service_id`: Orleans service ID.

### Pub/Sub Routing

- `grace__pubsub__system`: Pub/Sub provider selector (e.g., `AzureServiceBus`).

### Auth (OIDC / Auth0)

- `grace__auth__oidc__authority`
- `grace__auth__oidc__audience`
- `grace__auth__oidc__cli_client_id`
- `grace__auth__oidc__cli_redirect_port`
- `grace__auth__oidc__cli_scopes`
- `grace__auth__oidc__m2m_client_id`
- `grace__auth__oidc__m2m_client_secret`
- `grace__auth__oidc__m2m_scopes`

### Auth (Microsoft, Deprecated)

- `grace__auth__microsoft__client_id`
- `grace__auth__microsoft__client_secret`
- `grace__auth__microsoft__tenant_id`
- `grace__auth__microsoft__authority`
- `grace__auth__microsoft__api_scope`
- `grace__auth__microsoft__cli_client_id`

### Auth (PAT Defaults)

- `grace__auth__pat__default_lifetime_days`
- `grace__auth__pat__max_lifetime_days`
- `grace__auth__pat__allow_no_expiry`

### CLI / Client

- `GRACE_SERVER_URI`: Grace server base URL (must include port, no trailing
  slash).
- `GRACE_TOKEN`: PAT used for non-interactive auth.
- `GRACE_TOKEN_FILE`: Override for the token file path.

### Reminders

- `grace__reminder__batch__size`: Batch size for reminder processing.

### Diagnostics

- `grace__debug_environment`: Debug environment marker (e.g., `Local`, `Azure`).
- `grace__log_directory`: Grace server log directory.

### Future / Placeholders

- `grace__aws_sqs__queue_url`
- `grace__aws_sqs__region`
- `grace__gcp__projectid`
- `grace__gcp__topic`
- `grace__gcp__subscription`
