# Durable Workflows

Rules for grains, retries, and the workflows whose state must survive restarts.

## Identity of a durable decision

Every durable decision has an explicit, stable identity tuple. Name it in issues, tests, and review prompts:

- the actor or durable entity key;
- the operation ID or client decision ID;
- the dimensions that tell a repeat of the same request apart from a stale or conflicting one;
- the durable source of truth.

Prove retry, stale-attempt, and recovery behavior by reconstructing state from persisted history; a transient cache is
not evidence of a durable fact.

## Retries and background work

- Retry scheduling is durable, bounded, observable, and safe after a process restart.
- When retries must use the original decision inputs, persist the payload and the rule or policy snapshot before
  processing.
- Batch drains have explicit limits and clear partial-failure behavior.

## Webhooks and approvals

A webhook observes committed facts; an approval policy gates a proposed transition; an approval request is the runtime decision Grace generates for one attempt. The rules that shape code:

- Store the delivery payload and webhook rule snapshot before outbound dispatch; editing a rule later never changes an
  already-scheduled delivery. Delivery claims use stable dedupe keys.
- Webhook delivery is post-event; it never blocks or authorizes the source workflow.
- Approval request decisions are idempotent by client decision ID and responder identity. Approval requests are produced
  by policy or workflow behavior, not created through a public command.
- Approval notification delivery is a separate contract from webhook delivery and stays out of webhook delivery routes
  and commands.

## Manifest-backed storage

- `ManifestEligibilityPolicy` is applied when a `FileVersion` is created; later policy changes don't alter existing
  file versions.
- `UploadSession` is temporary coordination state, never durable content identity. `ManifestContributionWorkflow` owns
  contribution progress; `FileManifest` holds no per-repository batch progress.
- The SDK uploads only missing blocks, confirms them, and finalizes without reposting block bytes.
- Dedupe discovery returns bounded candidate windows and must not become a content-existence oracle.
- Range claims read authoritative `ContentBlockMetadata` at claim time. Finalization validates reconstruction before
  accepting a durable manifest identity.

Behavior that depends on emulators, Service Bus, storage, or Redis needs integration proof in `Grace.Server.Tests`.
