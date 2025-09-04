# Grace.SDK — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
Client-side SDK used by CLI and other clients.

## Key patterns
- Thin wrappers around server endpoints and actor proxies.
- DTOs are records with clear field names.

## Project rules for agents
1. Maintain public API stability; add new API surfaces over changing existing ones.
2. Add unit tests with mocks for the transport layer.
