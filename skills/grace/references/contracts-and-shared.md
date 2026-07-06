
# Contracts And Shared Surface

Load this reference for changes to `Grace.Types`, `Grace.Shared`, DTOs, domain events, parameters, serializers, shared
helpers, or constants.

## Contract Defaults

- Treat `src/Grace.Types` as the canonical schema source for IDs, records, discriminated unions, commands, events, DTOs,
  and durable state.
- Keep request and parameter classes in `src/Grace.Shared/Parameters` aligned with contract changes.
- Keep domain contracts free of HTTP-handler concerns.
- Prefer additive or versioned changes unless the active specification requires a breaking change.
- Keep `MessagePack`, Orleans, and `System.Text.Json` attributes intentional.
- Keep type defaults deterministic and complete.

## Shared Helpers

- Prefer adding narrowly named helpers instead of changing semantics of widely used helpers.
- Coordinate semantic helper changes with `Grace.Server`, `Grace.SDK`, `Grace.CLI`, and tests.
- Keep authentication helpers and parameters pure and reusable where possible.
- Environment variable names live in `src/Grace.Shared/Constants.Shared.fs`; do not duplicate raw strings when the
  constants exist.

## Authorization Contracts

Key files:

- `src/Grace.Types/Authorization.Types.fs`
- `src/Grace.Shared/Authorization.Shared.fs`
- `src/Grace.Shared/Parameters/Access.Parameters.fs`
- `src/Grace.Authorization.Tests/AuthorizationSemantics.Tests.fs`
- `src/Grace.Authorization.Tests/PathPermissions.Tests.fs`

When operations or roles change:

- Update role definitions and applies-to scope rules together.
- Check `ApprovalPolicyManage`, `ApprovalRequestRead`, `ApprovalRequestRespond`, `WebhookManage`, and
  `WebhookDeliveryRead` semantics when approval or webhook behavior changes.
- Add or update authorization semantic tests for conservative grants and denied actions.

## Webhook And Approval Contracts

Key files:

- `src/Grace.Types/Webhooks.Types.fs`
- `src/Grace.Shared/Parameters/Webhook.Parameters.fs`
- `src/Grace.Shared/Parameters/Approval.Parameters.fs`
- `src/Grace.Types.Tests/Webhooks.Types.Tests.fs`

Important vocabulary:

- A webhook rule configures delivery.
- A webhook delivery records a delivery attempt and retry state.
- An approval policy defines the requirement.
- An approval request is the durable decision workflow.
- Approval request decisions must be idempotent by client decision ID and responder identity.
- An approval notification delivery is distinct from a webhook delivery. The current public surface stores and validates
  approval policy notification URLs, but does not expose public approval-notification delivery query commands or routes.

Avoid adding an `approval request create` public command or endpoint unless a current product decision explicitly
reopens that boundary. Generated approval requests are seeded or produced by policy/workflow behavior.

## Manifest-Backed Storage Contracts

Key files:

- `CONTEXT.md`
- `src/Grace.Types/Common.Types.fs`
- `src/Grace.Types/UploadSession.Types.fs`
- `src/Grace.Types/ContentBlockMetadata.Types.fs`
- `src/Grace.Types/ManifestContributionWorkflow.Types.fs`
- `src/Grace.Types/RepositoryContentCounter.Types.fs`

Guardrails:

- `WholeFileContent` handles small or regular files.
- `FileManifest` handles large manifest-backed content.
- `ManifestEligibilityPolicy` is evaluated when creating a `FileVersion`; existing file versions do not change when the
  policy changes later.
- `FileManifest` identity comes from reconstruction content, not repository policy.
- `ContentBlockAddress` comes from ordered chunk addresses and compact block format version.
- `UploadSession` is temporary coordination state. It must not become the durable content identity.
- `ManifestContributionWorkflow` owns bounded contribution progress; do not store per-repository batch progress inside
  `FileManifest`.

## Validation

- Add focused tests in `Grace.Types.Tests` for pure contract semantics.
- Add consumer tests when the contract affects server, SDK, CLI, actor, or authorization behavior.
- Run focused tests first, then the selected Grace validation profile.

## Contract Propagation Map

For every public or durable contract change, record each applicable surface as updated, unchanged, waived, or deferred:

- `Grace.Types` DTOs, commands, events, persisted state, serializers, defaults
- `Grace.Shared` parameters and helpers
- server parsing, validation, authorization, route metadata, and error envelope
- CLI parser, JSON output, stdout/stderr, help, schema, and examples
- SDK/facade client and generated clients
- static OpenAPI component and aggregate documents
- events, webhooks, SignalR, watch, search, and projections
- tests, docs, ADRs, and agent guidance

Accepted inputs must be either implemented, rejected with a clear error, or explicitly documented as informational. Do
not silently accept no-op values.
