# Grace Project Map

Load this reference when locating code, choosing ownership boundaries, or orienting in an unfamiliar Grace surface.

## First Reads

- `CONTEXT.md` defines current product and architecture vocabulary.
- `README.md` explains the public product model and current developer workflow.
- `src/AGENTS.md` gives code-wide F# and validation expectations.
- Project-level `AGENTS.md` files give local rules.

## Main Projects

| Area | Paths | Notes |
| ---- | ----- | ----- |
| Domain contracts | `src/Grace.Types` | DTOs, events, IDs, discriminated unions, serialization shape |
| Shared helpers | `src/Grace.Shared` | Parameters, auth helpers, constants, cross-cutting utilities |
| HTTP API | `src/Grace.Server` | Giraffe handlers, startup, authorization, runtime services |
| Orleans actors | `src/Grace.Actors` | Grain interfaces and durable workflow implementations |
| SDK | `src/Grace.SDK` | Thin client calls aligned to server routes and DTOs |
| CLI | `src/Grace.CLI` | System.CommandLine commands, Spectre.Console output, local state DB |
| AppHost | `src/Grace.Aspire.AppHost` | Local/Azure topology, emulators, forwarded env vars |
| Server tests | `src/Grace.Server.Tests` | Aspire-hosted HTTP integration and server-surface actor coverage |
| Authorization tests | `src/Grace.Authorization.Tests` | RBAC, endpoint manifest, PAT, path permissions |
| CLI tests | `src/Grace.CLI.Tests` | Parsing, command routing, local history/config behavior |
| Type tests | `src/Grace.Types.Tests` | Deterministic pure contract semantics |

## Useful Search Patterns

Use `rg` with concrete paths. On Windows, do not rely on wildcard path expansion.

```powershell
rg -n "module EndpointAuthorizationManifest|endpoint " src/Grace.Server src/Grace.Authorization.Tests
rg -n "type .*Command|type .*Event|type .*Dto" src/Grace.Types
rg -n "route \"/|subRoute" src/Grace.Server/Startup.Server.fs
rg -n "Approval|Webhook|PromotionSet|WorkItem|Queue|Review" src
rg -n "UploadSession|ContentBlock|ManifestContribution|RepositoryContentCounter" src
```

## Current High-Signal Surfaces

- Authorization: `src/Grace.Shared/Authorization.Shared.fs`,
  `src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs`,
  `src/Grace.Authorization.Tests`.
- Webhooks and approvals: `src/Grace.Types/Webhooks.Types.fs`, `src/Grace.Shared/Parameters/Approval.Parameters.fs`,
  `src/Grace.Shared/Parameters/Webhook.Parameters.fs`, `src/Grace.Server/Approval*.fs`,
  `src/Grace.Server/Webhook*.fs`, `src/Grace.SDK/Approval.SDK.fs`, `src/Grace.SDK/Webhook.SDK.fs`.
- Candidate review model: `src/Grace.Types/Review.Types.fs`, `src/Grace.Server/Review*.fs`,
  `src/Grace.CLI/Command/Review.CLI.fs`, `src/Grace.CLI/Command/Candidate.CLI.fs`.
- Promotion flow: `src/Grace.Types/PromotionSet.Types.fs`, `src/Grace.Actors/PromotionSet.Actor.fs`,
  `src/Grace.Actors/PromotionQueue.Actor.fs`, `src/Grace.Server/PromotionSet.Server.fs`,
  `src/Grace.Server/Queue.Server.fs`.
- Manifest-backed storage: `CONTEXT.md`, `src/Grace.Types/UploadSession.Types.fs`,
  `src/Grace.Types/ContentBlockMetadata.Types.fs`, `src/Grace.Types/ManifestContributionWorkflow.Types.fs`,
  `src/Grace.Actors/UploadSession.Actor.fs`, `src/Grace.SDK/Storage.SDK.fs`.
- CLI local state: `.grace/grace-local.db` and sidecar files are internal; avoid treating them as repo changes.

## Boundary Rule

When changing one public surface, inspect its companions before stopping:

```text
Grace.Types -> Grace.Shared parameters -> Grace.Server -> Grace.SDK -> Grace.CLI -> focused tests -> docs/AGENTS
```
