# Grace.Server.Unit.Tests Agents Guide

Global policies live in `../AGENTS.md`; follow them before touching tests here.

## Purpose

- Unit, contract, determinism, and pure helper tests for server-adjacent behavior that does not boot Aspire.
- Keep coverage here free of HTTP-hosted server calls, emulator resources, Service Bus, blob storage, Redis, and
  `Grace.Server.Tests.Services`.
- Move ambiguous server behavior back to `Grace.Server.Tests` unless it is clearly no-Aspire and unit-shaped.
- This project is in the Fast validation gate and should stay independent of `Grace.Aspire.AppHost`.

## Test File Organization

- Preserve existing test names and assertion intent when moving coverage from `Grace.Server.Tests`.
- Keep files named for the behavior under test, even when the namespace still reflects historical test ownership.
- Do not add `AspireTestHost.fs` or references to `Grace.Aspire.AppHost`.
- Avoid adding assembly-level NUnit parallel defaults here for now. The current audit deferred the project because at
  least one fixture mutates process-static approval-store state.

## Validation

- Run targeted Fantomas formatting or checks before build and test validation after F# changes.
- Build `src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj` in Release before running project-specific
  `--no-build` tests.
- Run `dotnet test --configuration Release --no-build src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj`
  only after that build context exists.
- Run the smallest focused unit proof first. Fast is an optional broad preflight; GitHub `Validate` certifies the
  current pull-request revision. Fast uses a solution-level `dotnet test` filter; because several moved files keep
  historical `Grace.Server.Tests` namespaces, the Server.Unit selectors derive from project compile items and source
  declarations.
