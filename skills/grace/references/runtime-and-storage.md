
# Runtime And Storage

Load this reference for hosted services, background workers, webhooks dispatch, Service Bus, Aspire, storage, manifest
uploads, content-addressed storage, emulators, deployment/runtime configuration, or cancellation/retry behavior.

## Runtime Defaults

- Register hosted services intentionally and confirm lifetime, dependency-injection scope, and startup ordering.
- Thread cancellation tokens through queue reads, delays, HTTP calls, retries, batch drains, and shutdown paths.
- Make retry scheduling durable, bounded, observable, and safe after process restart.
- Drain batches with explicit limits, stable ordering where required, and clear partial-failure behavior.
- Persist payload and rule/policy snapshots before processing when retries must use the original decision inputs.
- Dispose HTTP responses and streams promptly.
- Redact secrets, tokens, URLs with credentials, and user-controlled payloads in logs and test output.

## Webhook Delivery

Key files:

- `src/Grace.Server/Webhook.Server.fs`
- `src/Grace.Server/WebhookDispatch.Server.fs`
- `src/Grace.Server.Tests/Webhook.Server.Tests.fs`
- `src/Grace.Server.Tests/WebhookDispatch.Server.Tests.fs`

Guardrails:

- Store delivery payload and webhook rule snapshot before outbound dispatch.
- Use stable dedupe keys for delivery claims.
- Keep retry state bounded and observable.
- Do not let current webhook rule edits mutate already-scheduled delivery semantics.
- Validate URL restrictions, event registry membership, and terminal status transitions.
- Webhook delivery is post-event and must not block or authorize the source workflow.
- Approval notification delivery is a separate contract from webhook delivery. Do not expose it through webhook delivery
  routes or CLI commands.

## Manifest-Backed Uploads

Key files:

- `CONTEXT.md`
- `src/Grace.SDK/Storage.SDK.fs`
- `src/Grace.Server/Startup.Server.fs`
- `src/Grace.Actors/UploadSession.Actor.fs`
- `src/Grace.Types/UploadSession.Types.fs`
- `src/Grace.Types/ContentBlockMetadata.Types.fs`
- `src/Grace.Types/ManifestContributionWorkflow.Types.fs`

Guardrails:

- Manifest-backed upload is default-on for eligible large files.
- The SDK plans local ContentBlocks, uploads only missing local blocks, confirms uploads, and finalizes the manifest
  without reposting already uploaded block payload bytes.
- Whole-file fallback is limited to ineligible files or recoverable manifest-upload failures.
- Dedupe discovery returns bounded candidate windows; it must not become an arbitrary content existence oracle.
- Range claims must use authoritative `ContentBlockMetadata` read at claim time.
- Finalization must validate reconstruction before accepting durable manifest identity.

## Aspire And Environment

Key files:

- `src/Grace.Aspire.AppHost/Program.Aspire.AppHost.cs`
- `src/Grace.Aspire.AppHost/AGENTS.md`
- `src/docs/ASPIRE_SETUP.md`
- `src/docs/ENVIRONMENT.md`
- `scripts/start-debuglocal.ps1`

AppHost currently coordinates local emulators and runtime wiring for:

- Cosmos DB
- Azurite / Azure Storage
- Service Bus emulator
- Redis
- Grace.Server

Relevant env vars live in `src/Grace.Shared/Constants.Shared.fs`, especially:

- `GRACE_SERVER_URI`
- `GRACE_TOKEN`
- `GRACE_TESTING`
- `GRACE_TEST_SKIP_SERVICEBUS`
- `GRACE_TEST_FIXED_PORTS`
- `grace__auth__oidc__*`
- `grace__authz__bootstrap__system_admin_*`
- `grace__azurecosmosdb__*`
- `grace__azure_storage__*`
- `grace__azure_service_bus__*`
- `grace__redis__*`

## Validation

- Use `pwsh ./scripts/validate.ps1 -Full` for Aspire, emulators, Service Bus, storage, Redis, deployment/runtime, or
  cross-service background behavior.
- Pair parser/unit tests with full-gate evidence when runtime behavior depends on external delivery infrastructure.

## Stale Authority And Materialization Ordering

Runtime, storage, and materialization changes must name the authority used for each side effect:

- current request body versus durable stored state
- current repository configuration versus recorded route or placement evidence
- local Watch status/cache versus re-read status after acquiring the mutation marker
- active lifecycle state versus retained retry or cleanup evidence
- authorized manifest/reference/path versus caller-supplied bytes, hashes, object keys, or FileVersion payloads

For each side effect, revalidate authority immediately before write, SAS issuance, materialization, publication, or
cleanup. Tests should prove stale snapshots cannot overwrite newer local state, mint object access, publish success, or
consume/retain pending work incorrectly.

Shared CAS and materialization work must authorize before materializing bytes and must not treat addresses, hashes,
object keys, or cache keys as capabilities unless a current product decision explicitly defines them as such.
