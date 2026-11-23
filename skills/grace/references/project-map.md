# Grace Project Map

Load this when locating code or choosing which project owns a change.

## First reads

- `CONTEXT.md`: product and architecture vocabulary.
- `README.md`: the public product model and developer workflow.
- `src/AGENTS.md` and the project `AGENTS.md` files.

## Projects

| Area | Path | Notes |
| ---- | ---- | ----- |
| Domain contracts | `src/Grace.Types` | DTOs, events, IDs, discriminated unions, serialization shape |
| Shared helpers | `src/Grace.Shared` | Parameters, auth helpers, constants |
| HTTP API | `src/Grace.Server` | Giraffe handlers, startup, authorization, runtime services |
| Orleans actors | `src/Grace.Actors` | Grain interfaces and durable workflows |
| Orleans codegen | `src/Grace.Orleans.CodeGen` | Generated serializers; keep in sync with grain and type changes |
| SDK | `src/Grace.SDK` | Client calls aligned to server routes and DTOs |
| CLI | `src/Grace.CLI` | Commands, Spectre.Console output, local state DB |
| CLI local DB worker | `src/Grace.CLI.LocalStateDb.Worker` | Background worker for the CLI's local SQLite state |
| Cache | `src/Grace.Cache`, `src/Grace.Cache.Storage` | Localhost-only read boundary and storage for verified cache artifacts |
| Operations | `src/Grace.Operations`, `.Data`, `.Worker` | Operations boundary, SQL data access, and worker (observations, measurements) |
| Load | `src/Grace.Load` | Load-generation tooling |
| AppHost | `src/Grace.Aspire.AppHost`, `src/Grace.Aspire.ServiceDefaults` | Local and Azure topology, emulators, forwarded settings |
| Static OpenAPI | `src/OpenAPI` | Public API description and generated-client inputs |
| Tests | `src/Grace.*.Tests` | See the table in `src/AGENTS.md` |

## Search patterns

Use `rg` with concrete paths; on Windows, don't rely on wildcard path expansion.

```powershell
rg -n "module EndpointAuthorizationManifest|endpoint " src/Grace.Server src/Grace.Authorization.Tests
rg -n "type .*Command|type .*Event|type .*Dto" src/Grace.Types
rg -n "route \"/|subRoute" src/Grace.Server/Startup.Server.fs
rg -n "UploadSession|ContentBlock|ManifestContribution|RepositoryContentCounter" src
```

## High-signal surfaces

- Authorization: `src/Grace.Shared/Authorization.Shared.fs`,
  `src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs`, `src/Grace.Authorization.Tests`.
- Webhooks and approvals: `src/Grace.Types/Webhooks.Types.fs`, `src/Grace.Shared/Parameters/Approval.Parameters.fs`,
  `src/Grace.Shared/Parameters/Webhook.Parameters.fs`, `src/Grace.Server/Approval*.fs`, `src/Grace.Server/Webhook*.fs`.
- Candidate review: `src/Grace.Types/Review.Types.fs`, `src/Grace.Server/Review*.fs`,
  `src/Grace.CLI/Command/Review.CLI.fs`, `src/Grace.CLI/Command/Candidate.CLI.fs`.
- Promotion: `src/Grace.Types/PromotionSet.Types.fs`, `src/Grace.Actors/PromotionSet.Actor.fs`,
  `src/Grace.Actors/PromotionQueue.Actor.fs`, `src/Grace.Server/PromotionSet.Server.fs`, `src/Grace.Server/Queue.Server.fs`.
- Manifest-backed storage: `src/Grace.Types/UploadSession.Types.fs`, `src/Grace.Types/ContentBlockMetadata.Types.fs`,
  `src/Grace.Types/ManifestContributionWorkflow.Types.fs`, `src/Grace.Actors/UploadSession.Actor.fs`,
  `src/Grace.SDK/Storage.SDK.fs`.
- Libraries: `docs/Libraries.Design.md`, `src/Grace.CLI/Library/`, `src/Grace.CLI/Command/Library.CLI.fs`.
