# Security And Auth

Load this reference for authentication, authorization, route protection, RBAC, PATs, OIDC, path permissions, secrets,
security-sensitive logging, or review of public access boundaries.

## Key Files

- `src/Grace.Types/Authorization.Types.fs`
- `src/Grace.Types/PersonalAccessToken.Types.fs`
- `src/Grace.Shared/Authorization.Shared.fs`
- `src/Grace.Shared/Parameters/Auth.Parameters.fs`
- `src/Grace.Shared/Parameters/Access.Parameters.fs`
- `src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs`
- `src/Grace.Server/Security/AuthorizationMiddleware.Server.fs`
- `src/Grace.Server/Security/PersonalAccessTokenAuth.Server.fs`
- `src/Grace.Server/Middleware/LogAuthorizationFailure.Middleware.fs`
- `src/Grace.Authorization.Tests`

## Route Authorization

- Every route must be covered by `EndpointAuthorizationManifest.Server.fs`.
- Keep manifest entries synchronized with `Startup.Server.fs`.
- Add or adjust tests in `Grace.Authorization.Tests` when route protection changes.
- For object-specific actions, load the stored object and authorize against its authoritative scope.
- For list actions, filter results before returning them.

## RBAC And Path Permissions

- Role catalog and effective operation logic live in `Grace.Shared.Authorization`.
- Check both coarse scope roles and path-level permissions when changing path-sensitive behavior.
- Watch for cross-scope object IDs, stale branch/repository IDs, and mixed-case role IDs.
- Approval and webhook operations are intentionally conservative:
  - `ApprovalPolicyManage`
  - `ApprovalRequestRead`
  - `ApprovalRequestRespond`
  - `WebhookManage`
  - `WebhookDeliveryRead`

## Authentication

- PATs use the `GracePat` auth scheme and `/auth/token/*` endpoints.
- `GRACE_TOKEN` accepts Grace PATs only.
- OIDC configuration is exposed through `/auth/oidc/config`.
- Auth0/OIDC settings use `grace__auth__oidc__*` environment keys.
- TestAuth is controlled through local/test configuration; verify `GRACE_TESTING=1` before assuming it is available.
- Local token files are disabled in the CLI. Do not re-enable local token persistence without a current product decision.

## Secret Handling

- Redact authorization headers, tokens, connection strings, SAS URLs, and credential-bearing URLs in logs and tests.
- Prefer constants from `Constants.EnvironmentVariables` over raw environment key strings.
- In PowerShell scripts, avoid passing SAS URLs through unescaped inline `--settings`; use JSON files when needed.
- Webhook URLs and approval policy notification URLs must use public HTTPS by default. Unsafe loopback URLs require a
  Development host environment, server opt-in, and explicit per-request acknowledgement.
- CLI JSON output for webhook rules and approval policies must redact destination URLs and signing secret material.

## Security Test Checklist

Add or update tests for:

- endpoint manifest coverage and duplicates
- claim mapping
- role semantics
- path permissions
- PAT creation, expiry, revocation, and no-expiry policy
- stored-object authorization after load
- list filtering
- redaction in logs or diagnostics
