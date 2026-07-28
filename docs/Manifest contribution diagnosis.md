# Manifest contribution diagnosis

Grace provides a bounded, read-only diagnostic for manifest contribution accounting. Use it when a repository manifest
counter, exact relationship, or background contribution workflow appears inconsistent.

The diagnostic does not repair or delete anything. It reads current actor state, exact relationships, counters,
workflows, and optional short-lived Redis evidence. It then writes a self-verifying JSON report for the operator.

## Requirements

- A running Grace Server
- A bearer token with `SystemAdmin`
- PowerShell 7
- One target selector
- An explicit relationship limit from 1 through 5000
- An existing output directory

Set the server and token before running the script.

PowerShell:

```powershell
$env:GRACE_SERVER_URI = "http://localhost:5000"
$env:GRACE_TOKEN = "<system-admin-token>"
```

bash / zsh:

```bash
export GRACE_SERVER_URI="http://localhost:5000"
export GRACE_TOKEN="<system-admin-token>"
```

The script never prints the token or writes it into the report.

## Diagnose one target

Choose exactly one of the following selector forms.

### Reference

Use a Reference when you want Grace to rebuild expected relationships from the Reference's current root
DirectoryVersion and its reachable DirectoryVersions.

```powershell
pwsh ./scripts/diagnose-manifest-contribution.ps1 `
  -RepositoryId 11111111-1111-1111-1111-111111111111 `
  -ReferenceId 22222222-2222-2222-2222-222222222222 `
  -MaxRelationships 5000 `
  -OutputPath ./artifacts/manifest-contribution-reference.json
```

`RepositoryId` is an optional qualifier for a Reference. When supplied, the current Reference actor must belong to that
Repository.

### DirectoryVersion

Use a DirectoryVersion when you want Grace to rebuild expected relationships from that current DirectoryVersion and
its reachable subtree.

```powershell
pwsh ./scripts/diagnose-manifest-contribution.ps1 `
  -RepositoryId 11111111-1111-1111-1111-111111111111 `
  -DirectoryVersionId 33333333-3333-3333-3333-333333333333 `
  -MaxRelationships 5000 `
  -OutputPath ./artifacts/manifest-contribution-directory-version.json
```

`RepositoryId` is an optional qualifier for a DirectoryVersion.

### Repository manifest counter

Use the complete counter tuple when the suspicious item is already known.

```powershell
pwsh ./scripts/diagnose-manifest-contribution.ps1 `
  -RepositoryId 11111111-1111-1111-1111-111111111111 `
  -StoragePoolId default `
  -ManifestAddress 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef `
  -MaxRelationships 5000 `
  -OutputPath ./artifacts/manifest-contribution-counter.json
```

A counter tuple has no source actor from which Grace can discover relationships that are missing from the exact
projection. Grace therefore reports all bounded facts it can read but returns `IncompleteRetain`.

### Counter operation

Use a deterministic repository counter operation identity when it is the only known starting point.

```powershell
pwsh ./scripts/diagnose-manifest-contribution.ps1 `
  -RepositoryContentCounterOperationId `
    "directory-version:33333333333333333333333333333333:default:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:add" `
  -MaxRelationships 5000 `
  -OutputPath ./artifacts/manifest-contribution-operation.json
```

The operation must use Grace's current `directory-version:<id>:<pool>:<manifest>:add|remove` form. Grace reads the
named DirectoryVersion and uses current actor state to check the operation identity. One operation still cannot prove
that every possible source actor is known, so its terminal result remains conservative.

## Exit codes

The script writes the complete JSON report atomically and verifies its SHA-256 before returning codes 0, 2, or 3.

| Code | Outcome | Meaning |
| ---: | --- | --- |
| 0 | `VerifiedComplete` | Bounded source-backed reads were complete and the observed evidence agrees. |
| 2 | `IncompleteRetain` | Useful evidence was collected, but Grace cannot safely claim completeness. Retain data. |
| 3 | `FailedRetain` | The selected source could not produce a usable current diagnosis. Retain data. |
| 4 | No trusted report | Input, authorization, transport, bound, response, SHA-256, or file writing failed. |

Treat every nonzero result as a reason to retain the manifest and investigate. The route never authorizes deletion by
itself.

## Read the report

Start with these fields:

- `Outcome` gives the terminal result.
- `ReclamationPermitted` is `true` only when source-backed evidence is complete and all relevant stored and rebuilt
  counts are zero.
- `EvidenceGaps` explains what Grace could not prove.
- `UnknownFields` names report facts that cannot be complete for this selector.
- `MissingRelationships` lists actor-supported relationships absent from the exact projection.
- `StaleRelationships` lists projected relationships no longer supported by the source DirectoryVersion actor.
- `CountEvidence` compares each durable counter with a complete actor rebuild when one is possible.
- `ActorFacts` contains every actor snapshot read by the diagnostic.
- `RedisEvidence` reports the optional recent operation result. `AbsentOrUnavailable` does not hide durable evidence.
- `RepairTargets` identifies the exact follow-up operation without executing it.
- `ReportSha256` protects the complete report content. The script verifies it before replacing `OutputPath`.

Actor snapshots are observations made during the bounded diagnostic. They are not a transaction or a durable audit
record.

## Interpret repair targets

The diagnostic can emit the following repair target prefixes:

- `GetOrAddExactRelationship:` identifies an actor-supported exact relationship that is missing.
- `RemoveStaleExactRelationship:` identifies an exact relationship whose source DirectoryVersion no longer supports
  it.
- `ReconcileCounter:` identifies a repository manifest counter that differs from a complete actor rebuild.
- `DiagnoseReadableSourceRequired:` means the selector cannot identify a complete missing set. Supply a readable
  Reference or DirectoryVersion before attempting repair.

Do not translate an `IncompleteRetain` report into a guessed repair. The repair commands and their verification steps
belong to the manifest contribution repair workflow tracked by Grace issue #735.

## Bound failures

`MaxRelationships` applies cumulatively to distinct exact relationships verified or enumerated by the request. If the
diagnostic encounters another relationship after reaching the limit, the route rejects the request rather than
returning a report that looks complete.

Increase the bound only when the selected target is understood and remains operationally safe. The maximum is 5000.
The diagnostic has no repository-wide default scan.

## Troubleshooting

### HTTP 401 or 403

Confirm `GRACE_TOKEN` is current and grants `SystemAdmin` on `System`. The route is intentionally absent from public
OpenAPI and generated Grace clients.

### Exit code 3 for Reference or DirectoryVersion

Confirm the identifier and optional Repository qualifier. A cleared, deleted, empty, or mismatched source cannot
support a complete rebuild.

### Redis says `AbsentOrUnavailable`

Continue with the durable actor, exact relationship, counter, and workflow evidence in the report. Redis only holds a
short-lived recent-operation result and is not a source of manifest membership.

### The output file did not change

The script replaces the requested file only after receiving a successful response, verifying its SHA-256, and mapping
the outcome. Invocation failures return exit code 4 and preserve the prior report.
