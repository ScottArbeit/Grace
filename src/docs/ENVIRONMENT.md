# Grace Environment Inventory

This document summarizes environment dependencies, onboarding variables, and runtime settings used by Grace.
Primary sources are `src/Grace.Shared/Constants.Shared.fs` (`EnvironmentVariables`) and Aspire host
configuration in `src/Grace.Aspire.AppHost/Program.Aspire.AppHost.cs`.

## Docker Dependencies (Local Aspire)

Local Aspire runs containers and emulators for:

- Azurite (Azure Storage emulator: blob, queue, table)
- Azure Cosmos DB emulator
- Azure Service Bus emulator (with a SQL Server container)
- Redis

The Aspire dashboard defaults to <http://localhost:18888> and OTLP export defaults to
`http://localhost:18889` unless overridden.

## Aspire Run Modes

- `ASPIRE_RESOURCE_MODE` (not in `EnvironmentVariables`):
  - `Local` (default): uses emulators and containers.
  - `Azure`: uses real Azure resources from config, user secrets, or environment.

## Test Toggles

- `GRACE_TESTING`: enables test mode in Aspire and local TestAuth paths.
- `GRACE_TEST_CLEANUP`: set to `1` or `true` to enable cleanup in server tests.

## Auth Forwarding In Aspire

`Grace.Aspire.AppHost` forwards these auth settings into `Grace.Server` when present:

- `grace__auth__oidc__authority`
- `grace__auth__oidc__audience`
- `grace__auth__oidc__cli_client_id` (publish mode)

## DebugLocal Onboarding Variables

For local onboarding, `scripts/start-debuglocal.ps1` is canonical.
`scripts/dev-local.ps1` remains a compatibility alias.

Bootstrap user resolution order is deterministic:

1. `-BootstrapUserId` script parameter.
2. First user in shell env `grace__authz__bootstrap__system_admin_users`.
3. First user in launch profile value
   `grace__authz__bootstrap__system_admin_users` from
   `src/Grace.Aspire.AppHost/Properties/launchSettings.json`.
4. `-BootstrapUserIdFallback` (defaults to `test-admin`).

The script prints both the resolved bootstrap user and the source used.

`GRACE_TESTING` behavior during onboarding:

- if `GRACE_TESTING` is already set in your shell, the script keeps that value;
- if unset, the script sets `GRACE_TESTING=1` for local TestAuth startup.

Debug/TestAuth mode is additive when OIDC is configured. Grace PAT bearer tokens still use the `GracePat` scheme,
interactive OIDC/MSA bearer tokens use JWT bearer validation, and explicit `x-grace-user-id` requests still use
TestAuth for first-time local bootstrap. `GRACE_TOKEN` remains PAT-only; do not put OIDC access tokens in it.

Use this path when you want zero external auth for a new local environment:

PowerShell:

```powershell
pwsh ./scripts/start-debuglocal.ps1 -GraceServerUri "http://localhost:5000" -SkipAuthProbe
```

bash / zsh:

```bash
pwsh ./scripts/start-debuglocal.ps1 --GraceServerUri "http://localhost:5000" -SkipAuthProbe
```

The script creates a PAT through explicit TestAuth headers so the first local SystemAdmin can get started without an
OIDC tenant. `-SkipAuthProbe` is required for this true zero-OIDC path because the default preflight checks
`/authenticate/oidc/config` before TestAuth PAT creation. Omit it when OIDC/MSA settings are configured and you want
the script to verify `/authenticate/oidc/config` and `/authenticate/me` before creating the PAT.

Use this path when the server has OIDC/MSA settings and you want normal interactive auth:

PowerShell:

```powershell
$env:GRACE_SERVER_URI="http://localhost:5000"
Remove-Item Env:GRACE_TOKEN -ErrorAction SilentlyContinue
grace authenticate login
grace authenticate whoami --output Verbose
grace authenticate token create --name "local-dev" --expires-in 30d --output Verbose
```

bash / zsh:

```bash
export GRACE_SERVER_URI="http://localhost:5000"
unset GRACE_TOKEN
grace authenticate login
grace authenticate whoami --output Verbose
grace authenticate token create --name "local-dev" --expires-in 30d --output Verbose
```

Authentication proves the caller identity. The first authenticated OIDC/MSA caller still needs authorization: seed a
SystemAdmin assignment with `grace__authz__bootstrap__system_admin_users` or have an existing admin grant the needed
role before protected operations will succeed. An authenticated caller that maps to a Grace User can create a PAT before
RBAC grants; that PAT still authorizes protected operations only through Grace's normal authorization checks.

`GRACE_TOKEN` takes precedence over M2M and interactive credentials. Clear stale or revoked PAT values before testing
interactive OIDC/MSA login, otherwise the CLI may keep sending the stale PAT even after `grace authenticate login` succeeds.

Scope creation uses normal authorization in DebugLocal and production:

- owner creation requires `SystemAdmin` or the support operation `SystemOperate` on the system resource;
- organization creation requires `OwnerAdmin` or `OwnerWrite` on the parent owner;
- repository creation requires `OrganizationAdmin` or `OrganizationWrite` on the parent organization;
- branch creation requires `RepositoryAdmin` or `RepositoryWrite` on the parent repository.

After successful scope creation, Grace ensures the authenticated creator has effective admin authority on the new scope.
Grace creates a direct matching admin grant (`OwnerAdmin`, `OrganizationAdmin`, `RepositoryAdmin`, or `BranchAdmin`) only
when inherited admin authority does not already apply. Non-scope creation such as DirectoryVersion, directory save,
reference creation, artifacts, promotion sets, reminders, validation records, and work items does not create admin
grants.

## DebugLocal Reliability Switches

These are script parameters (not environment variables):

- `-SkipAuthProbe`: skip auth preflight (`/authenticate/oidc/config`, `/authenticate/me`).
- `-NoTokenBootstrap`: skip PAT creation.
- `-TokenBootstrapMaxAttempts`: set max token bootstrap attempts.
- `-TokenBootstrapInitialBackoffSeconds`: set initial retry backoff.
- `-StartupTimeoutSeconds`: set health wait timeout.
- `-CleanupWaitSeconds`: set process cleanup wait timeout.

PowerShell:

```powershell
pwsh ./scripts/start-debuglocal.ps1 `
  -TokenBootstrapMaxAttempts 5 `
  -TokenBootstrapInitialBackoffSeconds 2
```

bash / zsh:

```bash
pwsh ./scripts/start-debuglocal.ps1 \
  --TokenBootstrapMaxAttempts 5 \
  --TokenBootstrapInitialBackoffSeconds 2
```

## DebugLocal Diagnostics Artifacts

On failure, onboarding writes artifacts under `.grace/logs`:

- `start-debuglocal-<run-id>.stdout.log`
- `start-debuglocal-<run-id>.stderr.log`
- `start-debuglocal-<run-id>.failure.json`
- `start-debuglocal-<run-id>.runtime-metadata.json`

These capture failure classification, retryability, cleanup notes, and runtime metadata for troubleshooting.

## Environment Variables (Canonical List)

### Telemetry

- `grace__applicationinsightsconnectionstring`: Application Insights connection string (optional).

### Storage (Azure)

- `grace__azure_storage__connectionstring`: Azure Storage connection string.
- `grace__azure_storage__account_name`: Storage account name override (managed identity path).
- `grace__azure_storage__endpoint_suffix`: Storage endpoint suffix override.
- `grace__azure_storage__key`: Storage account key.
- `grace__azure_storage__directoryversion_container_name`: Directory version container name.
- `grace__azure_storage__diff_container_name`: Diff container name.
- `grace__azure_storage__zipfile_container_name`: Zip container name.

### Cosmos DB (Azure)

- `grace__azurecosmosdb__connectionstring`: Cosmos DB connection string.
- `grace__azurecosmosdb__endpoint`: Cosmos endpoint (managed identity path).
- `grace__azurecosmosdb__database_name`: Cosmos database name.
- `grace__azurecosmosdb__container_name`: Cosmos container name.

### Service Bus (Azure)

- `grace__azure_service_bus__connectionstring`: Service Bus connection string.
- `grace__azure_service_bus__namespace`: Service Bus namespace.
- `grace__azure_service_bus__topic`: Service Bus topic name.
- `grace__azure_service_bus__operational_facts_topic`: Service Bus topic for operational usage facts. Defaults to
  `grace-operational-facts` in Aspire-managed local and publish configurations.
- `grace__azure_service_bus__subscription`: Service Bus subscription name.

### Redis

- `grace__redis__host`: Redis host.
- `grace__redis__port`: Redis port.

### Orleans

- `orleans_cluster_id`: Orleans cluster ID.
- `orleans_service_id`: Orleans service ID.

### Pub/Sub Routing

- `grace__pubsub__system`: Pub/sub provider selector (for example, `AzureServiceBus`).

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

### Authorization (Bootstrap)

- `grace__authz__bootstrap__system_admin_users`: semicolon-delimited user IDs to seed SystemAdmin at system
  scope when no assignments exist.
- `grace__authz__bootstrap__system_admin_groups`: semicolon-delimited group IDs to seed SystemAdmin at system
  scope when no assignments exist.

### Metrics

- `grace__metrics__allow_anonymous`: when `true`, allows anonymous access to `/metrics` (default `false`).

### CLI / Client

- `GRACE_SERVER_URI`: Grace server base URL (include port, omit trailing slash).
- `GRACE_TOKEN`: Grace PAT for non-interactive auth. OIDC/MSA access tokens are not valid here. This value wins before
  M2M and interactive login.
- `GRACE_TOKEN_FILE`: not supported; local plaintext token file storage is intentionally disabled.

### Reminders

- `grace__reminder__batch__size`: batch size for reminder processing.

### Diagnostics

- `grace__debug_environment`: debug environment marker (for example, `Local`, `Azure`).
- `grace__log_directory`: Grace server log directory.

### Future / Placeholders

- `grace__aws_sqs__queue_url`
- `grace__aws_sqs__region`
- `grace__gcp__projectid`
- `grace__gcp__topic`
- `grace__gcp__subscription`
