# Actors And Durability

Load this reference for Orleans grain behavior, event-sourced transitions, idempotency, reminders, timers, durable
workflow state, or actor recovery behavior.

## Orleans Defaults

- Start from `src/Grace.Actors/AGENTS.md` and `src/AGENTS.md`.
- Introduce or extend command and event contracts before changing grain logic.
- Keep transitions deterministic and safe for retries.
- Update event application and state write paths together.
- Preserve correlation IDs through actor calls and diagnostics.
- Reconstruct state from persisted history when validating retry, stale-attempt, or recovery behavior.
- Do not use transient caches as proof of durable facts.
- If grain interfaces or serializers change, confirm `Grace.Orleans.CodeGen` stays in sync.

## Idempotency Keys

Every durable decision should have an explicit, stable identity:

- actor or durable entity key
- operation ID or client decision ID
- identity dimensions that distinguish same request from stale or duplicate request
- durable source of truth

Use this tuple in issue bodies, tests, and review prompts.

## Current Actor Areas

| Actor Area | Key Files | Notes |
| ---------- | --------- | ----- |
| Access control | `AccessControl.Actor.fs` | Role assignments and path permissions |
| Approval requests | `ApprovalRequest.Actor.fs` | Decision idempotency, responder checks, history |
| PATs | `PersonalAccessToken.Actor.fs` | Token issuance/revocation state |
| Policy/review/queue | `Policy.Actor.fs`, `Review.Actor.fs`, `PromotionQueue.Actor.fs` | Candidate and gate workflow |
| Promotion sets | `PromotionSet.Actor.fs` | Conflict model, recompute/apply state |
| Work items | `WorkItem.Actor.fs`, `WorkItemNumber*.fs` | Links, attachments, numbers, summaries |
| Upload sessions | `UploadSession.Actor.fs` | Temporary upload coordination and cleanup reminders |
| Content block metadata | `ContentBlockMetadata.Actor.fs` | Whole-record metadata version and range presence |
| Manifest contribution | `ManifestContributionWorkflow.Actor.fs` | Bounded range contribution progress |

## Manifest Actor Guardrails

- `UploadSession` cleanup reminders delete only temporary coordination state after finalization, abandon, or expiration.
  They must not delete accepted manifests, content blocks, block metadata, or contribution accounting.
- `ContentBlockMetadataActor` is keyed by storage pool and content block address. Preserve whole-record
  `MetadataVersion` concurrency and exact range presence semantics.
- Compaction is a whole-record metadata rewrite that preserves the `ContentBlockAddress`.
- `ManifestContributionWorkflowActor` owns durable fan-out progress for range-count contribution. Repository-local
  reference counts stay in `RepositoryContentCounterActor`.

## Tests To Prefer

- Prefer server-surface integration tests for actor behavior unless project guidance says otherwise.
- Add actor-level tests only when the behavior cannot be tested cleanly through a stable public boundary.
- Include regression tests for duplicate operations, stale IDs, retries, cancellation, reordered events, reminder
  recovery, mutable config snapshots, and redaction when in scope.
