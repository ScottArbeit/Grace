# Work items

Work items are durable, event-sourced records for a unit of work in Grace.
They capture intent (title and description), status, notes, and links to
references, promotion sets, and reviewer artifacts.

## Canonical command and identifier behavior

- Use `grace workitem ...` as the canonical CLI path.
- Aliases (`work`, `work-item`, `wi`) remain supported for compatibility, but
  examples in docs should use `workitem`.
- Work item commands that take a `work-item` argument accept either:
  - a `WorkItemId` GUID, or
  - a positive `WorkItemNumber` (for example `42`).

## Server API surface

All work item routes are `POST` endpoints under `/work`.

- `/work/create`
- `/work/get`
- `/work/update`
- `/work/add-summary`
- `/work/link/reference`
- `/work/link/promotion-set`
- `/work/link/artifact`
- `/work/links/list`
- `/work/links/remove/reference`
- `/work/links/remove/promotion-set`
- `/work/links/remove/artifact`
- `/work/links/remove/artifact-type`
- `/work/attachments/list`
- `/work/attachments/show`
- `/work/attachments/download`

## CLI workflows

### Create and inspect work items

PowerShell:

```powershell
./grace workitem create `
  --title "Introduce baseline drift alerts" `
  --description "Add baseline drift detection and update review UI"

./grace workitem create `
  --work-item-id f88b46e2-5c36-4b52-9e36-716f7d7a9a8b `
  --title "Introduce baseline drift alerts"

./grace workitem show f88b46e2-5c36-4b52-9e36-716f7d7a9a8b
./grace workitem show 42

./grace workitem status f88b46e2-5c36-4b52-9e36-716f7d7a9a8b --set InReview
./grace workitem status 42 --set Done
```

bash / zsh:

```bash
./grace workitem create \
  --title "Introduce baseline drift alerts" \
  --description "Add baseline drift detection and update review UI"

./grace workitem create \
  --work-item-id f88b46e2-5c36-4b52-9e36-716f7d7a9a8b \
  --title "Introduce baseline drift alerts"

./grace workitem show f88b46e2-5c36-4b52-9e36-716f7d7a9a8b
./grace workitem show 42

./grace workitem status f88b46e2-5c36-4b52-9e36-716f7d7a9a8b --set InReview
./grace workitem status 42 --set Done
```

### Link references and promotion sets

PowerShell:

```powershell
./grace workitem link ref `
  f88b46e2-5c36-4b52-9e36-716f7d7a9a8b `
  f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c

./grace workitem link prset `
  42 `
  3d5c4d9a-0123-4567-89ab-987654321000
```

bash / zsh:

```bash
./grace workitem link ref \
  f88b46e2-5c36-4b52-9e36-716f7d7a9a8b \
  f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c

./grace workitem link prset \
  42 \
  3d5c4d9a-0123-4567-89ab-987654321000
```

### Attach summary, prompt, and notes content

PowerShell:

```powershell
./grace workitem attach summary 42 --file .\summary.md
./grace workitem attach prompt 42 --file .\prompt.md
./grace workitem attach notes 42 --text "Reviewer follow-up required before merge."
```

bash / zsh:

```bash
./grace workitem attach summary 42 --file ./summary.md
./grace workitem attach prompt 42 --file ./prompt.md
./grace workitem attach notes 42 --text "Reviewer follow-up required before merge."
```

### Retrieve reviewer attachments

PowerShell:

```powershell
./grace workitem attachments list 42
./grace workitem attachments show 42 --type summary --latest
./grace workitem attachments download 42 `
  --artifact-id 11111111-2222-3333-4444-555555555555 `
  --output-file .\summary.md
```

bash / zsh:

```bash
./grace workitem attachments list 42
./grace workitem attachments show 42 --type summary --latest
./grace workitem attachments download 42 \
  --artifact-id 11111111-2222-3333-4444-555555555555 \
  --output-file ./summary.md
```

### Inspect and clean up links

PowerShell:

```powershell
./grace workitem links list 42
./grace workitem links remove ref 42 f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c
./grace workitem links remove prset 42 3d5c4d9a-0123-4567-89ab-987654321000
```

bash / zsh:

```bash
./grace workitem links list 42
./grace workitem links remove ref 42 f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c
./grace workitem links remove prset 42 3d5c4d9a-0123-4567-89ab-987654321000
```

## SDK example (F#)

```fsharp
open Grace.SDK
open Grace.Shared.Parameters.WorkItem

let createParameters =
    CreateWorkItemParameters(
        WorkItemId = "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
        Title = "Introduce baseline drift alerts",
        Description = "Add baseline drift detection and update review UI",
        CorrelationId = "corr-0001"
    )

let! created = WorkItem.Create(createParameters)

let linksParameters =
    GetWorkItemLinksParameters(
        WorkItemId = "42",
        CorrelationId = "corr-0002"
    )

let! links = WorkItem.GetLinks(linksParameters)
```

## Current limitations

- Work item commands support reference and promotion-set links plus reviewer
  artifact links.
- Candidate, review packet, checkpoint, and gate-attestation link management is
  still internal and does not yet have dedicated public work-item link
  endpoints.
