# Authentication (Auth0 OIDC)

Grace uses Auth0 as the identity broker for CLI and API authentication.
The server accepts either a Grace PAT (`grace_pat_v1_...`) or a JWT access
token minted by Auth0 for the Grace API audience.

Interactive browser login is CLI-only in this phase. The server does not
host a web login flow.

## Auth0 tenant setup

You need three Auth0 applications: an API (audience), a native app for
the CLI, and an M2M app for agents.

### Grace API (Auth0 API / audience)

1. Create an API in Auth0.
2. Set the Identifier (Audience) to a stable value, for example
   `https://api.gracevcs.dev`.
3. Leave the token profile as Auth0 default or RFC 9068.

### Grace CLI (Auth0 native app)

1. Create a Native application in Auth0.
2. Add a loopback callback URL (fixed port):
   - `http://127.0.0.1:8400/callback`
3. Enable Authorization Code + PKCE and Device Authorization.
4. Enable Refresh Token Rotation and allow `offline_access` scope.

### Grace Agents (Auth0 M2M app)

1. Create a Machine to Machine application.
2. Authorize it to call the Grace API with appropriate scopes.

## Environment variables

### Grace Server

Required:

- `grace__auth__oidc__authority` = Auth0 issuer, e.g.
  `https://<tenant>.us.auth0.com/`
- `grace__auth__oidc__audience` = API audience, e.g.
  `https://api.gracevcs.dev`

### Grace CLI (interactive)

Required:

- `grace__auth__oidc__authority`
- `grace__auth__oidc__audience`
- `grace__auth__oidc__cli_client_id`

Optional:

- `grace__auth__oidc__cli_redirect_port` (default `8400`)
- `grace__auth__oidc__cli_scopes` (default `openid profile email
  offline_access`)

### Grace CLI (M2M)

Required:

- `grace__auth__oidc__authority`
- `grace__auth__oidc__audience`
- `grace__auth__oidc__m2m_client_id`
- `grace__auth__oidc__m2m_client_secret`

Optional:

- `grace__auth__oidc__m2m_scopes`

### PAT mode

- `GRACE_TOKEN` = Grace personal access token (PAT only).

`GRACE_TOKEN_FILE` and local plaintext token files are disabled.

## CLI usage

### Interactive login (humans)

Commands:

- `grace auth login` (default PKCE, falls back to device flow)
- `grace auth login --auth pkce`
- `grace auth login --auth device`
- `grace auth status`
- `grace auth whoami`
- `grace auth logout`

Interactive sessions require OS-backed secure storage (MSAL Extensions).
If secure storage is unavailable, use PAT or M2M.

### M2M login (agents)

Set the M2M environment variables and run any CLI command. Access tokens
are cached in-memory per CLI invocation only.

### PAT login (agents)

Set `GRACE_TOKEN` to a Grace PAT. Any non-PAT value is rejected.

## Server authentication

Grace.Server validates:

- PATs (`grace_pat_v1_...`) via the Grace PAT scheme.
- Auth0 JWT access tokens via JWT bearer validation with the configured
  authority and audience.

The `/auth/login` endpoint is informational only. It does not initiate
an interactive web login flow.
