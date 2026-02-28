# Continuous review

Continuous review tracks promotion-set candidates, evaluates gates, and records
review artifacts in a deterministic and auditable flow.

## Canonical workflow model

- Use `grace queue ...` for promotion-set queue operations.
- Use `grace candidate ...` for candidate-first reviewer operations:
  `get`, `required-actions`, `attestations`, `retry`, `cancel`, `gate rerun`.
- Use `grace review ...` for promotion-set scoped reviewer actions:
  `open`, `checkpoint`, `resolve`.
- Use `grace review report ...` for candidate-scoped report output:
  `show`, `export`.

## Current API surface

### Queue endpoints

- `POST /queue/status`
- `POST /queue/enqueue`
- `POST /queue/pause`
- `POST /queue/resume`
- `POST /queue/dequeue`

### Candidate and report endpoints

- `POST /review/candidate/get`
- `POST /review/candidate/retry`
- `POST /review/candidate/cancel`
- `POST /review/candidate/required-actions`
- `POST /review/candidate/attestations`
- `POST /review/candidate/gate-rerun`
- `POST /review/report/get`

### Review endpoints

- `POST /review/notes`
- `POST /review/checkpoint`
- `POST /review/resolve`
- `POST /review/deepen` (stub)

### Policy endpoints

- `POST /policy/current`
- `POST /policy/acknowledge`

## CLI examples

### Queue management

PowerShell:

```powershell
./grace queue enqueue `
  --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000 `
  --branch main `
  --policy-snapshot-id e3b0c44298fc1c149afbf4c8996fb924 `
  --work 42

./grace queue status --branch main
./grace queue pause --branch main
./grace queue resume --branch main
./grace queue dequeue --branch main --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000
```

bash / zsh:

```bash
./grace queue enqueue \
  --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000 \
  --branch main \
  --policy-snapshot-id e3b0c44298fc1c149afbf4c8996fb924 \
  --work 42

./grace queue status --branch main
./grace queue pause --branch main
./grace queue resume --branch main
./grace queue dequeue --branch main --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000
```

`--work` accepts either a GUID `WorkItemId` or a numeric `WorkItemNumber`.

### Candidate-first operations

PowerShell:

```powershell
./grace candidate get --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate required-actions --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate attestations --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate retry --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate gate rerun --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c --gate policy
```

bash / zsh:

```bash
./grace candidate get --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate required-actions --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate attestations --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate retry --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
./grace candidate gate rerun --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c --gate policy
```

### Promotion-set review actions

PowerShell:

```powershell
./grace review open --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000

./grace review checkpoint `
  --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000 `
  --reference-id f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c

./grace review resolve `
  --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000 `
  --finding-id 6e58b4de-7f3b-4a2b-9a6f-111111111111 `
  --approve `
  --note "Reviewed and acceptable."
```

bash / zsh:

```bash
./grace review open --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000

./grace review checkpoint \
  --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000 \
  --reference-id f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c

./grace review resolve \
  --promotion-set 3d5c4d9a-0123-4567-89ab-987654321000 \
  --finding-id 6e58b4de-7f3b-4a2b-9a6f-111111111111 \
  --approve \
  --note "Reviewed and acceptable."
```

### Report-first review output

PowerShell:

```powershell
./grace review report show --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c

./grace review report export `
  --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c `
  --format markdown `
  --output-file .\review-report.md
```

bash / zsh:

```bash
./grace review report show --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c

./grace review report export \
  --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c \
  --format markdown \
  --output-file ./review-report.md
```

### Source attribution in history

Use the global `--source` option to tag and filter automation activity.

PowerShell:

```powershell
./grace history show --source codex
./grace history search workitem --source codex
```

bash / zsh:

```bash
./grace history show --source codex
./grace history search workitem --source codex
```

You can also set the environment fallback:

PowerShell:

```powershell
$env:GRACE_SOURCE="codex"
./grace history show
```

bash / zsh:

```bash
export GRACE_SOURCE="codex"
./grace history show
```

## Current limitations and stubs

- `grace review inbox` is still a CLI stub.
- `grace review deepen` is still a CLI and server stub.
- Queue processing and automatic candidate state transitions are not fully
  orchestrated server-side; external automation is expected.
- Gate implementations are still partial for some gate types.
