# Grace.Authorization.Tests

Deterministic authorization contract tests: endpoint manifest coverage, duplicate routes, RBAC roles, claim mapping,
PATs, and path permissions. No HTTP host, Aspire, emulators, or Orleans runtime here; behavior that needs the hosted
HTTP pipeline belongs in `Grace.Server.Tests`.
