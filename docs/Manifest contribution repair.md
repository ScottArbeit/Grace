# Manifest contribution repair

Grace provides a bounded, dry-run-first repair for one verified
[manifest contribution diagnosis](Manifest%20contribution%20diagnosis.md). Use it only after reviewing the diagnostic
report and its concrete `RepairTargets`.

The repair route is internal, requires `SystemAdmin`, and is intentionally absent from public OpenAPI, the SDK, and
generated clients. It never scans a Repository and does not accept Cosmos DB, Service Bus, Redis, or storage
credentials.

## Requirements

- A running Grace Server
- A bearer token with `SystemAdmin`
- PowerShell 7
- An unchanged diagnosis JSON report
- The report's exact `ReportSha256`

Set the server and token.

```powershell
$env:GRACE_SERVER_URI = "http://localhost:5000"
$env:GRACE_TOKEN = "<system-admin-token>"
```

The script sends the report only to Grace Server. It does not connect directly to backing services.

## Start with a dry run

Dry run is the default. It validates the report schema and SHA-256, rereads bounded current Grace state, prints the
ordered proposed actions, and performs zero mutations.

```powershell
pwsh ./scripts/repair-manifest-contribution.ps1 `
  -ReportPath ./artifacts/manifest-contribution-diagnostic.json `
  -ExpectedReportSha256 "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
```

A dry run that still requires repair returns `IncompleteRetain` with exit code 2. Review `ProposedActions`,
`DiagnosisReportSha256`, and `Message` before execution.

Optionally save the terminal repair report atomically.

```powershell
pwsh ./scripts/repair-manifest-contribution.ps1 `
  -ReportPath ./artifacts/manifest-contribution-diagnostic.json `
  -ExpectedReportSha256 "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" `
  -OutputPath ./artifacts/manifest-contribution-repair-dry-run.json
```

## Execute the bounded plan

Mutation requires the explicit `-Execute` switch.

```powershell
pwsh ./scripts/repair-manifest-contribution.ps1 `
  -ReportPath ./artifacts/manifest-contribution-diagnostic.json `
  -ExpectedReportSha256 "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" `
  -Execute `
  -OutputPath ./artifacts/manifest-contribution-repair.json
```

Grace first requires current bounded evidence to match the signed report exactly. It rejects duplicate actions and
plans larger than twice the signed `MaxRelationships`. Each distinct signed action is attempted at most once, with a
target-specific source reread immediately before mutation. Grace then performs one final bounded diagnosis; only that
final `VerifiedComplete` result reports success.

If a dependency fails or cancellation arrives after partial progress, the result preserves the confirmed
`AppliedActions` prefix as `FailedRetain`. The current action may have an unknown outcome. Do not repeat `-Execute` with
the old report. Run a fresh diagnosis, review its new SHA-256 and plan, and start with another dry run.

## Exit codes

| Code | Outcome | Meaning |
| ---: | --- | --- |
| 0 | `VerifiedComplete` | Current post-repair bounded evidence is complete. |
| 2 | `IncompleteRetain` | Dry run found work, or current evidence cannot safely complete. Retain data. |
| 3 | `FailedRetain` | A repair dependency failed. Partial safe progress may exist; retain data. |
| 4 | No trusted report | Input, authorization, transport, schema, SHA-256, response, or output writing failed. |

Only exit code 0 means the repair route proved `VerifiedComplete`. A diagnosis report's `VerifiedComplete` value is
bounded evidence, not permission to reclaim data.

## Safety boundaries

The repair has five actions:

- Republish a live Reference actor's original `Created` event. Its deterministic broker `MessageId` remains
  `Reference/<ReferenceId>/Created`, and every `ReferenceType` follows the same path.
- Get or add one missing parent-DirectoryVersion relationship after the parent still names the child.
- Get or add one missing DirectoryVersion-manifest relationship after the DirectoryVersion still names the manifest.
  This projection-only repair does not rerun normal manifest accounting.
- Remove one proven stale DirectoryVersion-manifest relationship after current source absence and unchanged counter and
  workflow evidence are confirmed.
- Atomically replace one proven positive Repository logical manifest count at an expected actor revision.

Logical and physical contribution state stay separate. Repository count repair writes one
`RepositoryContentCounterActor` snapshot, advances its revision once, and records one bounded completed change. It emits
no contribution intent, starts no `ManifestContributionWorkflowActor`, and does not change `ContentBlockMetadata`.
Normal repository zero-crossing accounting remains the only path that changes physical StoragePool contribution.

There is no force-clear or resume command. The workflow adds no scheduler, durable repair queue, audit actor,
checkpoint, cursor, history, Repository-wide scan, reverse index, or elapsed-time release rule.

Rebuilt-zero repair is not admitted. Treat every nonzero result as retention guidance and run a fresh diagnosis rather
than guessing or resuming another mutation.
