# Authentication

This document describes how to configure and use authentication for Grace during development.

Grace supports two authentication mechanisms:

1. **Auth0 (OIDC/JWT bearer tokens)** for interactive developer login (PKCE or Device Code) and machine-to-machine (client credentials).
2. **Grace Personal Access Tokens (PATs)** for automation and non-interactive usage.

> Important: Authentication proves “who you are.” Authorization (RBAC and path permissions) determines “what you can do.” PATs do **not** introduce a separate permission model; they authenticate a principal that is then authorized via Grace’s normal authorization system.

---

## Quickstart for contributors (recommended path)

1. **Set up Auth0** (one-time) using the instructions in **Auth0 tenant setup** below.
2. **Run Grace.Server** (typically via Aspire) with OIDC env vars configured.
3. **Point the Grace CLI at your server** by setting `GRACE_SERVER_URI`.

PowerShell example:

```powershell
$env:GRACE_SERVER_URI="http://localhost:5000"
```

Bash / zsh example:

```bash
export GRACE_SERVER_URI="http://localhost:5000"
```

4. **Login via the CLI**:

   * `grace auth login` — Interactive login (tries PKCE first, then falls back to Device Code).
   * `grace auth whoami` — Verifies your identity against the running server.

---

## Auth0 tenant setup (development)

### What you need to create in Auth0

Grace needs these Auth0 resources:

1. **An Auth0 API (Resource Server)** for the Grace Server API

   * This provides the **Audience** value (the API Identifier).
2. **A Native Auth0 Application** for the Grace CLI (interactive login)

   * This provides the **CLI client ID**.
3. *(Optional)* **A Machine-to-Machine Auth0 Application** for CI/automation

   * This provides an **M2M client ID** and **M2M client secret**.

### Values you must capture from Auth0 (you will use these as env vars)

* **Tenant domain** (example: `my-tenant.us.auth0.com`)

  * Used to construct the **Authority**: `https://<tenant-domain>/`
* **API Identifier** (your “Audience”)

  * Example: `https://grace.local/api`
* **Native app Client ID** (Grace CLI interactive login)
* *(Optional)* **M2M app Client ID + Client Secret**

---

### Step 1: Create the Auth0 API (Resource Server)

Auth0 Dashboard steps:

1. Go to **Applications → APIs → Create API**.
2. Set:

   * **Name**: `Grace (Dev)` (or similar)
   * **Identifier** (this becomes the **Audience**): choose a stable string, e.g. `https://grace.local/api`
3. Enable **Allow Offline Access** on the API.

   * This is required so the CLI can receive refresh tokens (when requesting `offline_access`).
4. Save.

Record:

* API **Identifier** (Audience)
* Tenant Domain (Authority base)

---

### Step 2: Create the Auth0 Native Application (Grace CLI)

Auth0 Dashboard steps:

1. Go to **Applications → Applications → Create Application**.

2. Choose application type: **Native**.

3. In the application settings:

   * Ensure **Authorization Code** (PKCE) is enabled.
   * Ensure **Refresh Token** grant is enabled.
   * Ensure **Device Code** grant is enabled (so the CLI can use device flow on headless systems).

4. Configure **Allowed Callback URLs** to include the CLI callback URL:

   * Default Grace CLI callback URL:

     * `http://127.0.0.1:8391/callback`

   If you override the CLI redirect port (via `grace__auth__oidc__cli_redirect_port`), you must also update this callback URL accordingly.

5. Configure refresh token behavior (recommended for development):

   * Enable refresh token rotation (or equivalent Auth0 setting) and ensure refresh tokens are issued to the application.

Record:

* Native application **Client ID** (this is `grace__auth__oidc__cli_client_id`)

---

### Step 3 (optional): Create the Auth0 M2M Application (automation)

Auth0 Dashboard steps:

1. Create a new application of type **Machine to Machine**.
2. Authorize it to call your **Grace API (Resource Server)**.
3. Choose scopes if you’ve defined API scopes (Grace does not currently require Auth0 API scopes for authorization decisions, but your tenant policies may).
4. Record:

   * **Client ID** (`grace__auth__oidc__m2m_client_id`)
   * **Client Secret** (`grace__auth__oidc__m2m_client_secret`)

---

## Grace CLI authentication modes

The CLI can authenticate in multiple ways. The first matching mode “wins”:

1. **PAT mode** if `GRACE_TOKEN` is set (must be a Grace PAT, prefix `grace_pat_v1_`).
2. **Error** if `GRACE_TOKEN_FILE` is set (local token storage is intentionally disabled).
3. **M2M mode** if M2M env vars are set.
4. **Interactive mode** if you have logged in previously (token stored in OS secure store).

### Primary CLI commands

* `grace auth login [--auth pkce|device]`
  Interactive login to Auth0; stores access/refresh tokens in the OS secure store. If `--auth` is not specified, the CLI attempts PKCE and falls back to Device Code.

* `grace auth status`
  Shows whether the CLI currently has usable credentials (PAT, M2M, or interactive).

* `grace auth whoami`
  Calls the server and prints the authenticated identity information.

* `grace auth logout`
  Clears the cached interactive token from the secure store.

---

## Personal Access Tokens (PATs)

PATs are bearer tokens issued by Grace and validated by Grace. They are typically used for:

* CI jobs and automation
* running the CLI in non-interactive environments
* scripting against the Grace HTTP API

A PAT string looks like:

* `grace_pat_v1_<...>`

### Creating a PAT

You must already be authenticated (interactive Auth0 login, or an existing PAT, or M2M) to create a PAT.

**CLI (recommended)**

* `grace auth token create --name "<token-name>"`
  Creates a PAT with the server-default lifetime.

* `grace auth token create --name "<token-name>" --expires-in 30d`
  Creates a PAT that expires after the given duration. Supported suffixes: `s`, `m`, `h`, `d`.

* `grace auth token create --name "<token-name>" --no-expiry`
  Creates a non-expiring PAT **only if** the server allows it.

**Notes**

* The PAT value is a secret. Store it in a secret manager.
* Treat PATs like passwords; do not commit them into git.

### Using a PAT

Set the token in your environment:

PowerShell example:

```powershell
$env:GRACE_TOKEN="grace_pat_v1_..."
```

Bash / zsh example:

```bash
export GRACE_TOKEN="grace_pat_v1_..."
```

Then run any CLI command as usual; authentication will use the PAT automatically.

If you are calling the HTTP API directly, use the standard Authorization header:

* `Authorization: Bearer grace_pat_v1_...`

### Listing and revoking PATs

* `grace auth token list`
  Lists active PATs for the current principal.

* `grace auth token list --all`
  Lists active + expired + revoked tokens.

* `grace auth token revoke <token-id>`
  Revokes a PAT by token ID (GUID). Revoked tokens are no longer accepted.

* `grace auth token status`
  Shows whether the current `GRACE_TOKEN` is present and parseable.

### How PAT “permissions” work in Grace

PATs do **not** have an independent “permission set” like some systems (GitHub fine-grained tokens, etc.). Instead:

* A PAT authenticates a principal (usually a user).
* Grace authorization is then evaluated normally:

  * **RBAC roles** assigned to the principal (user or group) at a scope
  * **Path permissions** keyed by “claims” and/or group membership

Important implementation detail:

* When a PAT is created, Grace snapshots the current principal’s `grace_claim` values and `grace_group_id` values into the token record on the server.
* If your group membership or claim set changes later, existing PATs will **not** automatically pick up those changes. Create a new PAT if you need a token that reflects updated claims/groups.

### Setting permissions for a PAT

Because a PAT’s authorization comes from the principal it authenticates, you “set PAT permissions” by granting/revoking roles and path permissions for that principal.

Primary CLI commands for authorization management:

* `grace access grant-role ...`
  Grants a role to a principal at a scope (Owner/Organization/Repository/Branch/System).

* `grace access revoke-role ...`
  Revokes a role from a principal at a scope.

* `grace access list-role-assignments ...`
  Lists role assignments at a scope (optionally filtered by principal).

* `grace access upsert-path-permission ...`
  Sets or updates a path permission entry in a repository.

* `grace access remove-path-permission ...`
  Removes a path permission entry.

* `grace access list-path-permissions ...`
  Lists path permissions, optionally scoped to a path prefix.

* `grace access check ...`
  Asks the server “would this principal be allowed to do operation X on resource Y?”

> For detailed role IDs and operations, use `grace access list-roles` and consult the authorization types in `Grace.Types.Authorization`.

---

## Environment variables

This section documents auth-related environment variables used by Grace Server and the Grace CLI.

### Grace CLI (always relevant)

* `GRACE_SERVER_URI` (required for CLI)
  **No default.** Must point to the running Grace server base URI.
  Source: Aspire dashboard output, local run output, or your deployment URL.

Example value:

* `http://localhost:5000`

PowerShell example:

```powershell
$env:GRACE_SERVER_URI="http://localhost:5000"
```

Bash / zsh example:

```bash
export GRACE_SERVER_URI="http://localhost:5000"
```

---

### Grace Server OIDC configuration (Auth0/JWT)

These variables enable Auth0 JWT authentication on the server.

* `grace__auth__oidc__authority` (optional, enables OIDC when set)
  **No default.**
  Source: your Auth0 tenant domain. Use `https://<tenant-domain>/`.

* `grace__auth__oidc__audience` (required if `...__authority` is set)
  **No default.**
  Source: Auth0 API Identifier (Resource Server “Identifier”).

Recommended (for CLI auto-config):

* `grace__auth__oidc__cli_client_id` (optional for server auth; required for `/auth/oidc/config`)
  **No default.**
  Source: Auth0 Native app Client ID.

> If `grace__auth__oidc__authority` and `grace__auth__oidc__audience` are not set, the server will fall back to PAT-only authentication.

---

### Grace CLI interactive OIDC configuration (Auth0 login)

The Grace CLI can authenticate interactively using Auth0 (OIDC). There are two ways to supply the required OIDC settings:

1. **Recommended:** have the CLI fetch OIDC settings from the Grace Server.
2. **Advanced:** configure OIDC settings directly on the CLI via environment variables.

---

#### Recommended: CLI auto-configuration from the server

If you set only:

* `GRACE_SERVER_URI` — Base URI of the running Grace Server (example: `http://localhost:5000`)

…then the CLI can fetch the OIDC configuration automatically by calling:

* `GET /auth/oidc/config` — Returns the server’s OIDC settings needed for interactive login.

This works **only if** the server is configured with OIDC and has the values needed to publish them (see server env vars below).

**How to use**

1. Start the server with OIDC enabled (Authority + Audience, and preferably the CLI client ID).

2. Set the server URI:

   PowerShell example:

   ```powershell
   $env:GRACE_SERVER_URI="http://localhost:5000"
   ```

   Bash / zsh example:

   ```bash
   export GRACE_SERVER_URI="http://localhost:5000"
   ```

3. Login:

   * `grace auth login` — Interactive Auth0 login (tries PKCE, then device flow).

4. Verify:

   * `grace auth whoami` — Calls the server and prints the authenticated identity.

**Server-side requirements for auto-configuration**

For `GET /auth/oidc/config` to return useful values, the server must be configured with:

* `grace__auth__oidc__authority` — `https://<tenant-domain>/`
* `grace__auth__oidc__audience` — Auth0 API Identifier
* `grace__auth__oidc__cli_client_id` — Auth0 Native app Client ID (recommended)

If the server is missing `grace__auth__oidc__cli_client_id`, the CLI may still be able to login if you supply the client ID locally (see Advanced).

---

#### Advanced: set OIDC settings on the client

If you cannot use server auto-configuration (for example, you are testing against an endpoint that does not expose `/auth/oidc/config`), you can configure the CLI directly via environment variables.

**Required**

* `grace__auth__oidc__authority`
  No default. Source: Auth0 tenant domain.
  Format: `https://<tenant-domain>/`

* `grace__auth__oidc__audience`
  No default. Source: Auth0 API Identifier (Resource Server “Identifier”).

* `grace__auth__oidc__cli_client_id`
  No default. Source: Auth0 Native app Client ID.

**Optional (recommended defaults)**

* `grace__auth__oidc__cli_redirect_port`
  Default: `8391`
  If you change this, you must also update the Auth0 Native app callback URL to:
  `http://127.0.0.1:<port>/callback`

* `grace__auth__oidc__cli_scopes`
  Default: `openid profile email offline_access`
  `offline_access` is required to receive refresh tokens.

**How to use**

1. Set environment variables.

   PowerShell example:

   ```powershell
   $env:GRACE_SERVER_URI="http://localhost:5000"
   $env:grace__auth__oidc__authority="https://<tenant-domain>/"
   $env:grace__auth__oidc__audience="https://grace.local/api"
   $env:grace__auth__oidc__cli_client_id="<native-client-id>"
   ```

   Bash / zsh example:

   ```bash
   export GRACE_SERVER_URI="http://localhost:5000"
   export grace__auth__oidc__authority="https://<tenant-domain>/"
   export grace__auth__oidc__audience="https://grace.local/api"
   export grace__auth__oidc__cli_client_id="<native-client-id>"
   ```

2. Login:

   * `grace auth login` — Interactive Auth0 login (tries PKCE, then device flow).

3. Verify:

   * `grace auth whoami` — Calls the server and prints the authenticated identity.

---

### Grace CLI machine-to-machine (M2M) configuration

If you set these env vars, the CLI will use Auth0 client credentials to obtain an access token.

* `grace__auth__oidc__authority`
  **No default.** Source: Auth0 tenant domain.

* `grace__auth__oidc__audience`
  **No default.** Source: Auth0 API Identifier.

* `grace__auth__oidc__m2m_client_id`
  **No default.** Source: Auth0 M2M application Client ID.

* `grace__auth__oidc__m2m_client_secret`
  **No default.** Source: Auth0 M2M application Client Secret.

Optional:

* `grace__auth__oidc__m2m_scopes`
  Default: empty
  Space-separated list of scopes to request (if your tenant requires/uses them).

PowerShell example:

```powershell
$env:grace__auth__oidc__authority="https://<tenant-domain>/"
$env:grace__auth__oidc__audience="https://grace.local/api"
$env:grace__auth__oidc__m2m_client_id="<m2m-client-id>"
$env:grace__auth__oidc__m2m_client_secret="<m2m-client-secret>"
# Optional:
$env:grace__auth__oidc__m2m_scopes="read:foo write:bar"
```

Bash / zsh example:

```bash
export grace__auth__oidc__authority="https://<tenant-domain>/"
export grace__auth__oidc__audience="https://grace.local/api"
export grace__auth__oidc__m2m_client_id="<m2m-client-id>"
export grace__auth__oidc__m2m_client_secret="<m2m-client-secret>"
# Optional:
export grace__auth__oidc__m2m_scopes="read:foo write:bar"
```

---

### Grace CLI PAT configuration

* `GRACE_TOKEN` (optional)
  **No default.**
  Source: output from `grace auth token create`.
  Must be a Grace PAT (prefix `grace_pat_v1_`). If set, this overrides interactive login and M2M.

* `GRACE_TOKEN_FILE`
  **Not supported.** Local plaintext token file storage is intentionally disabled.

---

### Grace Server PAT policy controls

These affect how the server handles PAT creation requests:

* `grace__auth__pat__default_lifetime_days`
  Default: `90`

* `grace__auth__pat__max_lifetime_days`
  Default: `365`

* `grace__auth__pat__allow_no_expiry`
  Default: `false`

---

## Getting Auth0 values automatically (CLI + APIs)

If you already have an Auth0 tenant configured, the **Auth0 CLI** can be used to find the exact values needed for Grace without clicking through the dashboard.

### Auth0 CLI login

* `auth0 login`
  Authenticates the Auth0 CLI for interactive use (or with client credentials for CI).

If you need additional Management API scopes, re-run login with scopes, e.g.:

* `auth0 login --scopes "read:client_grants,create:client_grants"`

### Find tenant information

* `auth0 tenants list --json`
  Lists tenants accessible to your Auth0 CLI session.

* `auth0 tenant-settings show --json`
  Shows tenant settings (useful to confirm you’re operating on the expected tenant).

### Find the Grace API audience (API Identifier)

* `auth0 apis list --json`
  Lists APIs (Resource Servers). Find your Grace API and read its `identifier`.

* `auth0 apis show <api-id|api-audience> --json`
  Shows details for a specific API.

### Find the CLI Client ID (Native app) and M2M credentials

* `auth0 apps list --json`
  Lists applications.

* `auth0 apps show <app-id> --json`
  Shows app details (including `client_id`).

* `auth0 apps show <app-id> --reveal-secrets --json`
  Shows app details **including secrets** (use only for M2M apps, and handle output carefully).

### Make raw Management API calls (advanced)

* `auth0 api get "tenants/settings"`
  Makes an authenticated request to the Auth0 Management API and prints JSON.

This is useful if you need endpoints not exposed by a dedicated `auth0 <noun> <verb>` command.

### Creating Auth0 resources via the Auth0 CLI (optional)

You can create Auth0 resources non-interactively.

* `auth0 apis create ...`
  Creates an API (Resource Server). You can set identifier (audience), token lifetime, and offline access.

* `auth0 apps create ...`
  Creates an application (Native / M2M / etc).

* `auth0 apps update ...`
  Updates an application (callbacks, grant types, refresh token config, etc).

> If you prefer the dashboard, you can ignore this section entirely.

---

## Troubleshooting

### `grace auth login` fails and mentions refresh tokens

Ensure:

* the Auth0 API has **Allow Offline Access** enabled
* the Auth0 Native app allows refresh tokens and is configured to issue them
* `grace__auth__oidc__cli_scopes` includes `offline_access`

### Headless environments / CI

Use:

* M2M auth (client credentials env vars), or
* PATs (`GRACE_TOKEN`), or
* `grace auth login --auth device` if interactive login is still acceptable.

```
```
