# Grace.Authorization.Tests Agents Guide

Global policies live in `../AGENTS.md`; follow them before touching tests here.

## Purpose

- Cover authorization-sensitive contracts, including endpoint manifest coverage, duplicate route declarations, RBAC role
  semantics, principal claim mapping, PAT behavior, and path permission decisions.
- Prefer this project for deterministic auth and authorization contract tests that do not need an HTTP host, Aspire
  resources, storage emulators, Service Bus, Redis, or Orleans runtime state.

## Project Rules

1. Keep route authorization tests synchronized with `Grace.Server/Security/EndpointAuthorizationManifest.Server.fs` and
   the route map in `Grace.Server/Startup.Server.fs`.
1. Use server unit tests for pure server helper behavior when the test naturally belongs beside server-adjacent code.
1. Use `Grace.Server.Tests` only when the behavior needs the hosted HTTP pipeline or Aspire-backed resources.
1. Do not log bearer tokens, PATs, Authorization headers, secrets, or credential-bearing URLs in test output.

## Validation

- Run targeted Fantomas formatting or checks before build/test validation after F# changes.
- Build `src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj` in Release before project-specific
  `--no-build` tests.
- Run the smallest focused authorization proof first. Fast is an optional broad preflight; GitHub `Validate` certifies
  the current pull-request revision across the repository.
