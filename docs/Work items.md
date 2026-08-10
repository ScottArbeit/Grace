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
- `/work/description/set`
- `/work/description/clear`
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
- `/work/attachments/delete`
- `/work/attachments/undelete`

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

./grace workitem set-status f88b46e2-5c36-4b52-9e36-716f7d7a9a8b --status InReview
./grace workitem set-status 42 -s Done
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

./grace workitem set-status f88b46e2-5c36-4b52-9e36-716f7d7a9a8b --status InReview
./grace workitem set-status 42 -s Done
```

### Set and clear the current description

`description set` replaces the current Markdown from exactly one non-empty source: `--text`, `--file`, or `--stdin`.
The CLI reads file and standard-input content completely before sending the unchanged text through the existing set
request; it does not trim, normalize line endings, or rewrite Unicode. Empty input, a missing or unreadable file, no
source, and multiple sources fail without a request. Use `description clear` for intentional removal. Both commands
accept a work-item GUID or positive number. Clear retains prior immutable description content, does not expose a public
history, and a later set becomes current in actor append order.

The system-wide description limit applies to both `description set` and create-with-description. It counts Unicode
scalar values; configure it for the `Grace.Server` process in the
[environment inventory](../src/docs/ENVIRONMENT.md#work-item-descriptions). There are no owner-level overrides.

PowerShell:

```powershell
./grace workitem description set 42 --text "Describe the next delivery slice."
./grace workitem description set 42 --file .\description.md
Get-Content -Raw .\description.md | ./grace workitem description set 42 --stdin
./grace workitem description clear 42
```

bash / zsh:

```bash
./grace workitem description set 42 --text "Describe the next delivery slice."
./grace workitem description set 42 --file ./description.md
cat ./description.md | ./grace workitem description set 42 --stdin
./grace workitem description clear 42
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

### Add summary, prompt, and notes attachments

Use `--type` to classify the attachment as a summary, prompt, or notes. Every supported type follows the same add,
upload, and link workflow and requires exactly one of `--file`, `--text`, or `--stdin`.

PowerShell:

```powershell
./grace workitem attachments add 42 --type summary --file .\summary.md
./grace workitem attachments add 42 --type prompt --file .\prompt.md
./grace workitem attachments add 42 --type notes --text "Reviewer follow-up required before merge."
```

bash / zsh:

```bash
./grace workitem attachments add 42 --type summary --file ./summary.md
./grace workitem attachments add 42 --type prompt --file ./prompt.md
./grace workitem attachments add 42 --type notes --text "Reviewer follow-up required before merge."
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

### Delete and recover attachments

Attachment deletion is separate from generic link cleanup. An attachment created through `attachments add` or
`agent add-summary` belongs to one work item and cannot be linked to a second work item.

Use `attachments delete` to logically delete one attachment. Grace requires a reason, keeps the bytes and owning link
for recovery, and hides the attachment from normal list, show, and download operations. The cleanup deadline is based
on the repository's stored `LogicalDeleteDays` value when deletion is accepted. The repository default is 30 days;
an override is honored and later policy changes do not alter an existing deletion deadline.

Use `attachments undelete` before that deadline to restore the attachment. If the deadline passes, durable cleanup
removes the blob, work-item link, and artifact state. A stale cleanup operation from an earlier deletion cannot remove
a restored or re-deleted attachment.

PowerShell:

```powershell
./grace workitem attachments delete 42 `
  --artifact-id 11111111-2222-3333-4444-555555555555 `
  --delete-reason "Superseded by the approved summary"

./grace workitem attachments undelete 42 `
  --artifact-id 11111111-2222-3333-4444-555555555555
```

bash / zsh:

```bash
./grace workitem attachments delete 42 \
  --artifact-id 11111111-2222-3333-4444-555555555555 \
  --delete-reason "Superseded by the approved summary"

./grace workitem attachments undelete 42 \
  --artifact-id 11111111-2222-3333-4444-555555555555
```

### Inspect and clean up nonattachment links

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

let clearDescriptionParameters =
    ClearWorkItemDescriptionParameters(
        WorkItemId = "42",
        CorrelationId = "corr-0002"
    )

let! cleared = WorkItem.ClearDescription(clearDescriptionParameters)

let linksParameters =
    GetWorkItemLinksParameters(
        WorkItemId = "42",
        CorrelationId = "corr-0003"
    )

let! links = WorkItem.GetLinks(linksParameters)

let deleteParameters =
    DeleteWorkItemAttachmentParameters(
        WorkItemId = "42",
        ArtifactId = "11111111-2222-3333-4444-555555555555",
        DeleteReason = "Superseded by the approved summary",
        CorrelationId = "corr-0004"
    )

let! deletion = WorkItem.DeleteAttachment(deleteParameters)
```

## Current limitations

- Work item commands support reference and promotion-set links plus reviewer
  artifact links.
- Candidate, review packet, checkpoint, and gate-attestation link management is
  still internal and does not yet have dedicated public work-item link
  endpoints.
