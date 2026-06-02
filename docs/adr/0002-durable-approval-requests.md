---
status: accepted
date: 2026-06-02
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use durable ApprovalRequest state for gated transitions

Grace will model approvals as durable `ApprovalRequest` state transitions created by Grace workflows. Webhooks remain
post-event notifications, and validation results remain evidence records. Neither synchronous webhook responses nor
generic validation-result records are approval authority.

## Context

Grace is adding webhooks and approvals to support automation around sensitive workflows such as `promotion-set.apply`.
The product invariant is:

> Webhooks observe committed facts. Approval policies gate proposed transitions. Approval requests are runtime
> decisions.

The first protected workflow is promotion set apply. A matching approval policy can require a responder before the apply
transition commits. That decision must be scoped to the repository, target branch, promotion set, steps computation
attempt, approval policy id, and approval policy version so an approval for stale work cannot authorize newer work.

Grace already has automation events and validation result records. Those are useful adjacent concepts, but they do not
represent a server-created pending decision that survives retries, process restarts, responder authorization checks, and
terminal state transitions.

## Decision

Grace will create or find a durable `ApprovalRequest` when a protected workflow reaches an approval gate and no valid
approval already exists for the exact current scope. The request owns the runtime decision state:

- `Pending`
- `Approved`
- `Rejected`
- `Expired`
- `Cancelled`
- `Superseded`

Approval responses are state transitions on the stored request. The responder must be authorized against the stored
scope and must match the request's stored responder selector. Duplicate identical responses can be idempotent, while
conflicting responses are rejected.

Promotion sets do not gain a `PendingApproval` status. List and show surfaces can expose a derived promotion set
approval summary sourced from approval request state.

## Rejected Alternatives

### Synchronous webhook responses

Grace will not block a workflow while waiting for a webhook receiver to return approval. A webhook is a post-event
outbound notification rule. Letting a receiver synchronously approve a proposed transition would mix notification
delivery with workflow authority, make retries ambiguous, and make approval state depend on a transient HTTP exchange.

### Generic validation result records

Grace will not treat generic validation-result records as approval authority. Validation results can provide evidence,
but they do not prove Grace created a pending request, selected the responder, captured the protected scope, or enforced
terminal approval transitions.

### PromotionSetStatus.PendingApproval

Grace will not encode pending approval as a promotion set status. Pending approval is derived from approval request
state for the current apply scope. Keeping that state separate prevents stale approvals and avoids confusing readiness
of the promotion set with readiness of a particular apply attempt.

## Consequences

This decision gives Grace a durable and auditable approval boundary:

- Approval decisions survive restarts and retries.
- Approval scope can include a steps computation attempt and policy version.
- Authorization uses the stored request scope instead of trusting response body data.
- Webhook delivery can fail or retry without changing source workflow state.
- Validation evidence can remain separate while future slices may link it to approval requests explicitly.

The cost is a dedicated approval request lifecycle and persistence model. Future implementation slices must add server,
authorization, actor, SDK, and CLI behavior around these contracts before the feature is complete.
