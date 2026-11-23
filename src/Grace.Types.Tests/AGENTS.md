# Grace.Types.Tests

Tests for `Grace.Types` contracts: DTO and event behavior, parsers, and type-level invariants.

- Match files to type files: `Queue.Types.fs` -> `Queue.Types.Tests.fs`.
- Use fixed timestamps and explicit GUIDs when assertions depend on exact values.
- Test contract behavior, not internals. No server or Aspire dependencies.
