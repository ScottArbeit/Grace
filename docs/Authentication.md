# Authentication (Microsoft MSA + Entra)

Grace currently supports Microsoft identity for both web and CLI clients. This uses:
- A **web app registration** for the Grace Server (confidential client).
- A **public client app registration** for Grace CLI (device code flow).

GitHub and Google sign-in are planned but not yet implemented. The `/auth/login` page will list those providers once configured.

## App Registrations (Azure Portal)

### 1) Grace Server (Web app, confidential client)
1. Create a new **App registration** (Accounts: "Accounts in any organizational directory and personal Microsoft accounts").
2. Add a **Web** redirect URI for your server:
   - Local dev: `https://localhost:5001/signin-oidc` (or your local HTTPS port).
   - Deployed: `https://<your-domain>/signin-oidc`.
3. Create a **Client secret** (Certificates & secrets).
4. **Expose an API**:
   - Application ID URI: `api://<server-client-id>` (or your preferred URI).
   - Add scope: `access`.

This produces:
- Server **Client ID**
- Server **Client Secret**
- API scope: `api://<server-client-id>/access` (default used by Grace)

### 2) Grace CLI (Public client)
1. Create a new **App registration** (same account type as above).
2. Add a **Mobile and desktop** redirect URI:
   - `http://localhost` (device code flow does not rely on redirect, but this is commonly used).
3. **API permissions**:
   - Add delegated permission for the server API scope: `access`.
   - Grant admin consent if required by your tenant.

This produces:
- CLI **Client ID**

## Environment Variables

### Grace Server
Required:
- `grace__auth__microsoft__client_id` = Server Client ID
- `grace__auth__microsoft__client_secret` = Server Client Secret

Optional:
- `grace__auth__microsoft__tenant_id` = Tenant ID (default: `common`)
- `grace__auth__microsoft__authority` = Authority override (default: `https://login.microsoftonline.com/{tenant_id}`)
- `grace__auth__microsoft__api_scope` = API scope override (default: `api://{client_id}/access`)
- `grace__auth__microsoft__cli_client_id` = CLI Client ID (optional but recommended to keep in the same config)

### Grace CLI
Required:
- `grace__auth__microsoft__cli_client_id` = CLI Client ID
- `grace__auth__microsoft__api_scope` = API scope (ex: `api://<server-client-id>/access`)

Optional:
- `grace__auth__microsoft__tenant_id` = Tenant ID (default: `common`)
- `grace__auth__microsoft__authority` = Authority override

## Using the Web Login

- Browse to `https://<server>/auth/login`
- Choose **Microsoft**
- A cookie session is established after successful login.

## Using the CLI (Device Code)

Common commands:
- `grace auth login` (device code sign-in)
- `grace auth status`
- `grace auth whoami`
- `grace auth logout`

Tokens are cached using MSAL in:
- `~/.grace/grace_msal_cache.bin`

Grace CLI attaches the Bearer token automatically for SDK calls once authenticated.

## Future Providers (GitHub, Google)

GitHub and Google providers are not wired yet. When added, they will appear in `/auth/login` with additional environment variables and app registration steps.
