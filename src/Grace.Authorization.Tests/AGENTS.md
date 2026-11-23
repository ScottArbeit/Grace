# Grace.Authorization.Tests

Deterministic authorization contract tests: endpoint manifest coverage, duplicate routes, RBAC roles, claim mapping,
PATs, and path permissions. No HTTP host, Aspire, emulators, or Orleans runtime here.

- Keep route tests in sync with `Grace.Server/Security/EndpointAuthorizationManifest.Server.fs` and the route map in
  `Grace.Server/Startup.Server.fs`.
- Behavior that needs the hosted HTTP pipeline belongs in `Grace.Server.Tests`.
- Never log bearer tokens, PATs, Authorization headers, or credential-bearing URLs in test output.
