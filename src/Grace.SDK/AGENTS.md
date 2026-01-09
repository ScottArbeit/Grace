# Grace.SDK Agents Guide

Consult `../AGENTS.md` for global policies before modifying the SDK.

## Purpose

- Offer stable client-side APIs used by the CLI and other consumers to
  interact with Grace services and actors.
- Provide thin wrappers around server endpoints and actor proxies while
  keeping DTOs aligned with shared domain types.

## Key Patterns

- DTOs are records with descriptive field names that map directly to
  `Grace.Types` definitions -- sync changes across both projects.
- Wrap HTTP calls and actor interactions in lightweight modules; avoid
  embedding heavy business logic inside transport layers.
- Favor additive API expansions or versioned members when evolution is
  required; deprecate before removing public surface area.
- Document notable auth, retry, or transport assumptions here to spare
  agents from scanning implementation details.

## Project Rules

1. Maintain public API stability; breaking changes require coordination
   with CLI and external clients plus versioned alternatives.
2. Align SDK method contracts, routes, and DTOs with the server; update
   integration tests or samples when they diverge.
3. Record any new surface area, release notes, or migration steps inside
   this file to keep future agents informed.

## Validation

- Cover new behaviors with unit tests that mock transport layers, actor
  proxies, or HTTP abstractions.
- Execute `dotnet build --configuration Release` and targeted tests
  before shipping.
- When changing public APIs, update consumer samples and note doc
  updates required during PR review.

## Notes

- Added SDK modules for WorkItem, Policy, Review, Queue, and Candidate
  APIs (January 6, 2026).
- Queue SDK adds pause/resume/dequeue helpers aligned to server routes
  (January 6, 2026).
