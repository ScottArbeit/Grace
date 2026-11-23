# Grace Source Guide

Rules for all code under `src/`. Project folders may add their own `AGENTS.md`. The root `AGENTS.md` and
`docs/Development process.md` cover workflow.

## F# conventions

- New source is F# unless the issue chooses otherwise.
- Use `task { }` for asynchronous code. Keep side effects at the edges; prefer immutable data, small pure functions,
  and dependencies passed as parameters.
- Inside `task { }` and `backgroundTask { }`, don't define `let rec` or use `for ... in ... do` loops; they trigger FS3511.
  Treat an FS3511 warning as a regression.
- Prefer `System.Collections.Generic` collections (`List<T>`, `Dictionary<K,V>`) unless F# collections add real
  pattern-matching value. Use `myList[0]`, not `myList.[0]`.
- Domain types live in `Grace.Types`, reusable helpers in `Grace.Shared` (check there before writing a new one), and
  orchestration in the owning project.
- New modules, types, functions, and members get a short `///` comment naming the Grace behavior, invariant, route, or
  state transition they own. Add inline comments only where the logic is not obvious.
- Keep secrets, tokens, and personal data out of logs; keep structured logging and correlation IDs intact.

## Changing a public or durable contract

A change to a DTO, event, persisted state, route, or command usually reaches several projects. Walk the chain and update
what it touches, or note why a surface is unaffected:

```text
Grace.Types -> Grace.Shared parameters -> Grace.Server (routes, validation, authorization, endpoint manifest)
  -> Grace.Actors -> Grace.SDK -> Grace.CLI (options, help, JSON output) -> static OpenAPI and generated clients
  -> events, webhooks, SignalR, Watch, search -> tests and docs
```

## Runtime, storage, and authorization work

Name the authoritative source for each decision. After any wait, re-read it before a consequential write,
materialization, SAS issuance, or publication, and test that a stale snapshot cannot win. Authorize before
materializing content or revealing that a resource exists.

## Where tests go

| Behavior | Project |
| --- | --- |
| `Grace.Types` contracts | `Grace.Types.Tests` (`X.Types.fs` -> `X.Types.Tests.fs`) |
| Authorization, endpoint manifest, PATs, claims, path permissions | `Grace.Authorization.Tests` |
| Server helpers and contracts that need no HTTP host, emulators, or Aspire | `Grace.Server.Unit.Tests` |
| HTTP flows, Aspire resources, storage routes, actor behavior through the server | `Grace.Server.Tests` |
| CLI parsing, command routing, local config and history | `Grace.CLI.Tests` |

Prefer testing actor behavior through the server surface. Write tests that fail if the bug comes back: positive,
negative, boundary, and retry or restart cases that the change actually introduces.

## Validation

Format, build, then test. From `./src`, format touched files with `dotnet tool run fantomas <files>` (or
`dotnet tool run fantomas --recurse .` for broad edits). Build the project in Release before a `--no-build` test run:

```powershell
dotnet build --configuration Release src/Grace.Types.Tests/Grace.Types.Tests.fsproj
dotnet test --configuration Release --no-build src/Grace.Types.Tests/Grace.Types.Tests.fsproj --filter "FullyQualifiedName~MyFixture"
```

Run the smallest test set that proves the change, then `git diff --check`. GitHub `Validate` runs the full suite on the
PR. `pwsh ./scripts/validate.ps1 -Fast` runs the non-Aspire tests (Authorization, CLI, Operations, Types, Server.Unit)
as one solution-level `dotnet test`; `-Full` runs every test project, including Aspire integration tests. Use them to
diagnose or when CI is unavailable, not as a routine extra step.

NUnit assembly-level parallelism is limited on purpose: `Grace.Server.Unit.Tests` and `Grace.CLI.Tests` still mutate
process-wide state, and `Grace.Server.Tests` shares Aspire resources. Don't enable it there without fixing that first.

## Handoff

Report changed files with line numbers, the exact commands you ran and their results, anything skipped, and remaining
risks.
