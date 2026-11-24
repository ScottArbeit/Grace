# Grace.Server

Hosts the Orleans cluster and the Giraffe HTTP API used by the CLI, SDK, and other clients.

- Every route needs an entry in `Security/EndpointAuthorizationManifest.Server.fs`; `Grace.Authorization.Tests` checks
  coverage and duplicates. For ID-based routes, load the stored object and authorize against its scope. Filter lists
  before returning them.
- Keep middleware order, configuration loading, options binding, health checks, and Orleans startup order intact when
  editing `Program.Server.fs`, `Startup.Server.fs`, or other startup modules.
- Server-hosted interactive login is disabled. Auth settings and the SystemAdmin bootstrap keys are in
  `src/docs/ENVIRONMENT.md`.
- Add route-level tests for new endpoints and for regressions you fix.
