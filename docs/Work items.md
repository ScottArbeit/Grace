
# Work items

Work items are durable, event-sourced records for tracking a unit of work in
Grace. They are the place to capture intent (title/description), status, notes,
and links to other artifacts (references, promotion groups, candidates, review
packets, checkpoints, gate attestations).

A work item is not a branch, a commit, or a review itself; it is the
organizational anchor that those artifacts can point to. This makes work items
the natural place for humans and agents to look first when they want to
understand "what is this change for?"

## What exists today

- A WorkItem actor stores a stream of WorkItem events and builds a WorkItemDto.
- The server exposes create/get/update plus two link operations: link reference
  and link promotion group.
- The CLI exposes `grace work` commands for the same surface area.
- The SDK exposes the same endpoints as typed methods.

## Core data model (WorkItemDto)

A WorkItemDto aggregates the current state from the event stream. Key fields:

- Identity: `WorkItemId`, `OwnerId`, `OrganizationId`, `RepositoryId`.
- Content: `Title`, `Description`.
- Status and metadata: `Status`, `CreatedBy`, `CreatedAt`, `UpdatedAt`.
- Notes: `Constraints`, `Notes`, `ArchitecturalNotes`, `MigrationNotes`.
- People/tags: `Participants`, `Tags`.
- Links to other artifacts: `ExternalRefs`, `BranchIds`, `ReferenceIds`,
  `PromotionGroupIds`, `CandidateIds`, `ReviewPacketIds`, `ReviewCheckpointIds`,
  `GateAttestationIds`.

Only a subset of those link fields are currently wired to server endpoints (see
"Current limitations" below).

## Status values

Valid statuses are defined in `Grace.Types.WorkItem.WorkItemStatus`:

- `Backlog`
- `Active`
- `Blocked`
- `InReview`
- `Done`
- `Canceled`

## Lifecycle and invariants

- A work item is created once. A second `Create` with the same `WorkItemId` is
  rejected.
- Updates require the work item to exist. The actor returns
  `WorkItemDoesNotExist` otherwise.
- Updates are per-field commands. If an update request has no fields, the server
  returns "No updates were provided."
- Correlation IDs are used for idempotency; duplicate correlation IDs are
  rejected.

## Server API surface

All work item endpoints are `POST` routes under `/work`.

- `POST /work/create` -> Create a new work item.
- `POST /work/get` -> Retrieve the work item DTO.
- `POST /work/update` -> Update title, description, status, and notes.
- `POST /work/link/reference` -> Link a reference to the work item.
- `POST /work/link/promotion-group` -> Link a promotion group to the work item.

Example: create a work item.

```json
{
  "WorkItemId": "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
  "Title": "Introduce baseline drift alerts",
  "Description": "Add baseline drift detection and update review UI",
  "OwnerId": "7b0a3be6-4e21-4d3a-8d2c-5f6f4fa0c1a1",
  "OrganizationId": "c7f5b0cb-5f66-4f7c-93a6-b0b0f3c5f6a0",
  "RepositoryId": "2e7d2c03-a950-4d64-9d1c-9c8b1cc8a9b9",
  "CorrelationId": "corr-0001"
}
```

Example: update status and notes.

```json
{
  "WorkItemId": "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
  "Status": "InReview",
  "Notes": "Waiting on policy gate rollout.",
  "CorrelationId": "corr-0002"
}
```

Example: link a reference.

```json
{
  "WorkItemId": "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
  "ReferenceId": "f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c",
  "CorrelationId": "corr-0003"
}
```

## CLI examples

Create a work item (let the CLI generate the ID):

```powershell
./grace work create `
  --title "Introduce baseline drift alerts" `
  --description "Add baseline drift detection and update review UI"
```

Create a work item with an explicit ID:

```powershell
./grace work create `
  --work-item-id f88b46e2-5c36-4b52-9e36-716f7d7a9a8b `
  --title "Introduce baseline drift alerts"
```

Show a work item:

```powershell
./grace work show f88b46e2-5c36-4b52-9e36-716f7d7a9a8b
```

Update status:

```powershell
./grace work status f88b46e2-5c36-4b52-9e36-716f7d7a9a8b --set InReview
```

Link a reference:

```powershell
./grace work link ref `
  f88b46e2-5c36-4b52-9e36-716f7d7a9a8b `
  f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c
```

Link a promotion group:

```powershell
./grace work link group `
  f88b46e2-5c36-4b52-9e36-716f7d7a9a8b `
  3d5c4d9a-0123-4567-89ab-987654321000
```

## SDK examples

Create a work item (F#):

```fsharp
open Grace.SDK
open Grace.Shared.Parameters.WorkItem

let parameters =
    CreateWorkItemParameters(
        WorkItemId = "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
        Title = "Introduce baseline drift alerts",
        Description = "Add baseline drift detection and update review UI",
        CorrelationId = "corr-0001"
    )

let! result = WorkItem.Create(parameters)
```

Link a reference (F#):

```fsharp
open Grace.SDK
open Grace.Shared.Parameters.WorkItem

let parameters =
    LinkReferenceParameters(
        WorkItemId = "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
        ReferenceId = "f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c",
        CorrelationId = "corr-0003"
    )

let! result = WorkItem.LinkReference(parameters)
```

## Common workflows

Create -> link artifacts -> update status:

1) Create a work item for a new change.
2) Link the reference that represents the change set.
3) Link the promotion group that will land the change.
4) Update status as the change moves through review and promotion.

Example with IDs:

- Create: `f88b46e2-5c36-4b52-9e36-716f7d7a9a8b`
- Link reference: `f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c`
- Link promotion group: `3d5c4d9a-0123-4567-89ab-987654321000`
- Update status to `InReview` then `Done`

## Current limitations and gaps

- Only reference and promotion group linking are exposed via the server and CLI
  today.
- The WorkItem domain supports linking candidates, review packets, checkpoints,
  and gate attestations, but there are no public endpoints for those links yet.
- Work item linkage does not automatically happen when you enqueue a candidate;
  if you want a work item to point at a candidate or review packet, that must be
  done by an internal service or a future endpoint.
