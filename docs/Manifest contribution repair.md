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

Grace rereads the specific source, exact relationship, counter, and workflow evidence before every mutation. Unknown,
unreadable, incomplete, or changed evidence retains current state. After the applied prefix, Grace performs another
bounded reread; only that post-repair `VerifiedComplete` result reports success.

If a dependency fails after partial progress, the result is `FailedRetain`. Preserve the original report and inspect
current diagnosis. After the dependency is healthy, repeating `-Execute` with that report can resume only the exact
deterministic counter workflow left by its completed repair command. Any other changed evidence retains without a write.

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

The repair can only resend a deterministic event, ensure one exact relationship present or absent, or reconcile one
bounded repository-manifest counter through its existing actor operations.

There is no force-clear or resume command. The workflow adds no scheduler, durable repair queue, audit actor,
checkpoint, cursor, history, Repository-wide scan, reverse index, or elapsed-time release rule.

Treat every nonzero result as retention guidance. Investigate the current report rather than guessing another mutation.
