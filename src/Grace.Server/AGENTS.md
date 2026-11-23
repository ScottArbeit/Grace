# Grace.Server

Hosts the Orleans cluster and the Giraffe HTTP API used by the CLI, SDK, and other clients.

- Handlers follow the Giraffe style (`fun next ctx -> task { ... }`). Keep them small and delegate to modules.
- Every route needs an entry in `Security/EndpointAuthorizationManifest.Server.fs`; the authorization tests check
  coverage and duplicates. For ID-based routes, load the stored object and authorize against its scope. Filter lists
  before returning them.
- Keep middleware order, configuration loading, options binding, health checks, and Orleans startup order intact when
  editing `Program.Server.fs`, `OrleansConfig.fs`, or startup modules.
- Keep structured logging and correlation IDs. Authorization headers are redacted in request logs.
- Auth: Auth0 JWT bearer validation uses `grace__auth__oidc__authority` and `grace__auth__oidc__audience`. PATs use the
  `GracePat` scheme, the `/authenticate/token/*` routes, and `grace__auth__pat__*` lifetime settings. Server-hosted
  interactive login is disabled.
- Integration tests can seed a SystemAdmin at system scope with `grace__auth__bootstrap__system_admin_user_id`.
- Internal SystemAdmin diagnostic and observation routes document their scope, revalidation, and cancellation rules in
  `///` comments on their handlers. Read them before changing those routes.
- Add route-level tests for new endpoints and for regressions you fix.
