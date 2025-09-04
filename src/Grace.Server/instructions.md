# Grace.Server — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
Orleans host, HTTP handlers (Giraffe), and server startup logic.

## Key patterns
- `task { }` CE used for HTTP handlers and async orchestration.
- Giraffe `HttpHandler` pattern (fun next ctx -> task { ... }).
- Structured logging and defensive top-level `try/with` for handlers.

## Project rules for agents
1. Add route-level unit tests and, when practical, lightweight integration tests using in-memory Orleans clusters.
2. Preserve startup ordering and configuration loading when modifying `Program.Server.fs` or `OrleansConfig.fs`.
