# Codex Plan

## Plan Steps

1. Status: Done — Inventory repo instructions and locate auth/authz code/tests (RoleCatalog, PermissionEvaluator, Startup routes, TestAuth) and existing test projects.
2. Status: Done — Choose endpoint security manifest approach and add guardrail test that fails when routes lack explicit security classification.
3. Status: Done — Implement fast authorization semantics tests: RBAC matrix, PermissionEvaluator behavior, RoleCatalog regression (RepoAdmin includes BranchAdmin), FsCheck invariants.
4. Status: Done — Implement fast authn/claims/path/PAT tests: claims mapping, group/claim dedup, path permission robustness, PAT parsing fuzz.
5. Status: Done — Extend TestAuth to inject group claims via header and add integration harness support.
6. Status: Done — Implement integration bootstrap config and `/access/*` grant/revoke/list scope-boundary tests plus escalation prevention cases.
7. Status: Done — Implement `/access/checkPermission` integration tests for self/group/other principal disclosure controls.
8. Status: Done — Implement representative endpoint enforcement tests (authn/authz), allow-anonymous routes, and metrics policy coverage.
9. Status: Done — Wire fast test project into validate scripts/solution and ensure references/packaging are correct.
10. Status: Done — Ran `dotnet build -c Release` and `dotnet test -c Release`; integration tests still failing because Grace.Server fails to start in Aspire (see Decision Log).

## Decision Log

2026-01-18 03:13 — Created implementation plan and will prefer an explicit EndpointSecurityPolicy list if one does not already exist; will adapt to existing route security map if present.
2026-01-18 03:16 — Tried `bd create` for this work but beads is not initialized (missing issue_prefix). Proceeding without beads and will note in final summary.
2026-01-18 03:22 — Chose guardrail approach option (2): keep routes in Startup, add an AuthorizationMap manifest list, and have fast tests parse Startup.Server.fs to ensure every route has an explicit classification.
2026-01-18 04:40 — Moved bootstrap integration tests into a dedicated `Grace.Server.Bootstrap.Tests` project to avoid conflicts with the global Aspire host fixture.
2026-01-18 04:42 — Aspire integration tests still fail because Grace.Server exits during startup (exit code -1, empty logs); left failures documented after multiple retries.
