# Grace.Server Agents Guide

Global policies live in `../AGENTS.md`. Review them before touching server
code.

## Purpose

- Host the Orleans cluster, expose HTTP endpoints via Giraffe, and orchestrate
  startup and configuration for the Grace backend.
- Provide the primary HTTP surface area consumed by the CLI, SDK, and other
  integrations.

## Key Patterns

- Use the `task { }` computation expression for async flows. Keep HTTP handlers
  small and delegate complex logic to separate modules.
- Follow the Giraffe `HttpHandler` style (`fun next ctx -> task { ... }`) and
  guard handler bodies with defensive `try/with` blocks when needed.
- Preserve structured logging (including correlation IDs) and ensure middleware
  ordering remains stable.
- Keep configuration loading and Orleans startup sequencing intact. Changes
  should be intentional and well documented here.
- Coordinate contracts and message flows with `Grace.Actors`, `Grace.SDK`, and
  `Grace.Types` so that clients and grain logic remain in sync.
- Personal access tokens (PATs) use the `GracePat` auth scheme, `/auth/token/*`
  endpoints, and are governed by `grace__auth__pat__*` lifetime policies.
  Authorization headers are redacted in request logging.
- Auth0 JWT bearer validation is configured via
  `grace__auth__oidc__authority` and `grace__auth__oidc__audience`.
  Server-hosted interactive login is disabled in this phase.

## Project Rules

1. When modifying `Program.Server.fs`, `OrleansConfig.fs`, or startup modules,
   verify that ordering, options binding, and health checks remain correct.
2. Add route-level tests (and, when practical, in-memory Orleans integration
   tests) for new behaviors or regressions you fix.
3. Note any unusual hosting assumptions or deployment considerations here so
   agents avoid unnecessary spelunking.

## Validation

- Update or add tests covering new endpoints or handlers and run
  `dotnet test --no-build`.
- Rebuild the solution with `dotnet build --configuration Release` to catch
  dependency or configuration issues early.
- Consider running targeted integration smoke tests when touching Orleans
  clustering or storage configuration.
