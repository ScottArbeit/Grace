# Grace Doctor

`grace doctor` inspects the local Grace environment and reports diagnostics for people, scripts, and agents. The default
command is strictly read-only: it does not create configuration, initialize local state, migrate databases, repair
object-cache rows, acquire credentials, refresh tokens, open a browser, or upload support bundles. The explicit
`--repair-local-state` gesture is the sole exception and owns exact, non-destructive local-state reconstruction.

Use it when a Grace checkout, local state database, authentication environment, or server connection looks suspicious and
you need a structured first look before deciding what to fix.

PowerShell:

```powershell
grace doctor
grace --output Json doctor
grace doctor --list-checks
grace --output Json doctor --check Authentication --select Summary
grace doctor --repair-local-state
```

bash / zsh:

```bash
grace doctor
grace --output Json doctor
grace doctor --list-checks
grace --output Json doctor --check Authentication --select Summary
grace doctor --repair-local-state
```

## Command Behavior

- Default `grace doctor` runs the default local diagnostics and renders a human table when normal output is enabled.
- `--output Json` emits one `GraceReturnValue<DoctorReportDto>` JSON document to stdout.
- `--select` projects from `ReturnValue`, so use paths such as `Status`, `Summary`, or `Checks`, not
  `ReturnValue.Status`.
- `--schema` and `--examples` return machine-readable contract metadata for `DoctorReportDto` without running the
  diagnostics.
- Unknown `--check` tokens fail before diagnostics run and return the normal `GraceError` envelope in JSON mode.

The report version is `doctor-report-v1`. Consumers should use `ReportVersion` together with stable check IDs when
parsing or storing doctor evidence.

## Options

| Option | Behavior |
| --- | --- |
| `--full` | Includes full-profile checks, including working-tree scan and server probes unless `--offline` is used. |
| `--offline` | Skips network probes. Local checks can still run. |
| `--list-checks` | Lists the catalog and marks each returned check as skipped; diagnostic probes do not run. |
| `--check <id-or-category>` | Runs only matching check IDs or categories. Repeat the option or separate values with commas. |
| `--strict` | Returns exit code `1` when the report status is `Warning`; failures already return `1`. |
| `--repair-local-state` | Rebuilds exact status and the matching ordered event boundary without changing working-tree or server content. |

Check selection is case-insensitive. Category tokens can use spaces or hyphens, so `--check working-tree` matches
`working-tree.scan`. Exact check IDs can also select non-default or full-profile diagnostics without `--full`.
Default `grace doctor` does not run server probes. Explicit `--check Server` selection and `--full` can run server
checks such as `server.healthz.reachable`; `--offline` suppresses checks that do not support offline execution.

## Exit Codes

`grace doctor` uses diagnostic exit codes after successful command rendering:

| Report status | Default exit code | With `--strict` |
| --- | ---: | ---: |
| `Ok` | `0` | `0` |
| `Warning` | `0` | `1` |
| `Failed` | `1` | `1` |

Command-shape errors, including unknown check tokens or invalid `--select` paths, use the existing CLI error behavior.
In JSON mode they emit a `GraceError` document.

## JSON Contract

Successful JSON output uses the existing Grace result envelope. The `ReturnValue` is a `DoctorReportDto`:

```json
{
  "ReturnValue": {
    "ReportVersion": "doctor-report-v1",
    "Status": "Warning",
    "ExitCode": 0,
    "Full": false,
    "Offline": false,
    "Strict": false,
    "ListOnly": false,
    "RequestedChecks": [],
    "Catalog": [
      {
        "Id": "cli.catalog",
        "Category": "CLI",
        "Title": "CLI command catalog",
        "Description": "Verifies that the Grace CLI command catalog is available.",
        "DefaultEnabled": true,
        "SupportsOffline": true
      }
    ],
    "Checks": [
      {
        "Id": "cli.catalog",
        "Category": "CLI",
        "Title": "CLI command catalog",
        "Status": "Ok",
        "Severity": "Info",
        "Summary": "Doctor check catalog is available."
      }
    ],
    "Summary": {
      "Total": 1,
      "Ok": 1,
      "Warning": 0,
      "Failed": 0,
      "Skipped": 0
    }
  },
  "EventTime": "2026-06-10T00:00:00Z",
  "CorrelationId": "correlation-id",
  "Properties": []
}
```

Direct `grace --output Json doctor` output has an empty `Properties` array. The `cli.contractVersion` metadata appears
in schema and example introspection documents, not in the direct doctor execution envelope.

The important DTO fields are:

| Field | Meaning |
| --- | --- |
| `ReportVersion` | Stable report contract version. Current value: `doctor-report-v1`. |
| `Status` | Aggregate report status: `Ok`, `Warning`, or `Failed`. |
| `ExitCode` | Diagnostic exit code that the command returns after rendering succeeds. |
| `Full`, `Offline`, `Strict`, `ListOnly` | Echo the execution mode used to produce the report. |
| `RequestedChecks` | Normalized check tokens from `--check`. |
| `Catalog` | Catalog entries included in the selected profile. |
| `Checks` | Diagnostic result rows for selected checks. |
| `Summary` | Counts by result status. |

Check result statuses are `Ok`, `Warning`, `Failed`, and `Skipped`. Severity is currently `Info`, `Warning`, or
`Error`.

## Check Catalog

The v1 catalog contains 27 check IDs:

| Check ID | Category | Default | Offline | Summary |
| --- | --- | --- | --- | --- |
| `cli.catalog` | CLI | Yes | Yes | Verifies that the doctor check catalog is available. |
| `config.file.discover` | Configuration | Yes | Yes | Finds `.grace/graceconfig.json` without creating it. |
| `config.file.parse` | Configuration | Yes | Yes | Reads Grace configuration without rewriting or normalizing it. |
| `config.repository.identity` | Configuration | Yes | Yes | Checks configured owner, organization, repository, and branch identity. |
| `config.server-uri.valid` | Configuration | Yes | Yes | Validates the configured server URI without probing the server. |
| `server-uri.consistency` | Configuration | Yes | Yes | Compares `GRACE_SERVER_URI` with the configured server URI. |
| `user-config.file.discover` | User configuration | Yes | Yes | Looks for `~/.grace/userconfig.json` without creating it. |
| `user-config.file.parse` | User configuration | Yes | Yes | Reads user configuration without rewriting it. |
| `ignore.entries.parse` | Ignore | Yes | Yes | Reads `.graceignore` active entries without creating the file. |
| `auth.source.detected` | Authentication | Yes | Yes | Classifies likely local authentication source. |
| `auth.env-token.valid` | Authentication | Yes | Yes | Parses `GRACE_TOKEN` as a Grace PAT without printing or server-validating it. |
| `auth.token-file.unsupported` | Authentication | Yes | Yes | Reports unsupported `GRACE_TOKEN_FILE` configuration. |
| `auth.oidc.configuration` | Authentication | Yes | Yes | Checks OIDC environment completeness without token requests. |
| `state.db.file-present` | Local state | Yes | Yes | Checks the local state database path without creating files. |
| `state.db.read-only-open` | Local state | Yes | Yes | Opens SQLite local state in read-only mode. |
| `state.db.schema-version` | Local state | Yes | Yes | Reads the local state `schema_version` metadata. |
| `state.db.required-tables` | Local state | Yes | Yes | Verifies required local state tables. |
| `state.db.required-indexes` | Local state | Yes | Yes | Verifies required local state indexes. |
| `state.db.integrity-check` | Local state | Yes | Yes | Runs SQLite `integrity_check` read-only. |
| `state.db.foreign-key-check` | Local state | Yes | Yes | Runs SQLite `foreign_key_check` read-only. |
| `object-cache.index-readable` | Object cache | Yes | Yes | Reads object-cache metadata tables without repairing rows. |
| `working-tree.scan` | Working tree | Yes | Yes | Skipped by default; runs with `--full` or explicit selection. |
| `identity.auth-session` | Identity | No | No | Reserved for a later authentication diagnostic. |
| `server.connectivity` | Server | No | No | Reserved compatibility catalog entry. |
| `server.healthz.reachable` | Server | No | No | Probes `/healthz` with a short timeout. |
| `server.lifecycle.headers` | Server | No | No | Surfaces Grace SDK lifecycle response headers from `/healthz`. |
| `server.auth-principal.available` | Server | No | No | Probes `/authenticate/me` only when a valid non-refreshing `GRACE_TOKEN` PAT is present. |

## Working Tree Scan

The working-tree scan is intentionally visible but controlled:

- Default `grace doctor` includes `working-tree.scan` as a skipped row so users can see why the expensive scan did not
  run.
- `grace doctor --full` runs the scan from a read-only local-state snapshot and reports drift as a warning.
- `grace doctor --check working-tree.scan` and category selection with `--check working-tree` run the scan without
  requiring `--full`.
- Missing, unreadable, or incomplete local-state snapshots are reported inside the doctor report as skipped diagnostic
  rows when they are prerequisites, not as command-shape `GraceError`s.
- Actual scan execution errors are failures, including strict exact-check mode.
- Directory `.graceignore` entries prune scan traversal. File-only patterns that happen to match directory names do not
  prune whole directories.

When the scan reports drift, remediation points to `grace maintenance scan` for detailed differences or
`grace maintenance update-index` when you intentionally want to refresh the local maintenance index. Doctor does not
invoke either command.

## Authentication And Redaction

Doctor can help explain local authentication setup without exposing secrets:

- `GRACE_TOKEN` is parsed with the pure Grace PAT parser. The raw token is not printed.
- `Bearer <token>` form is accepted for PAT shape validation, and the raw bearer value is not printed.
- `GRACE_TOKEN_FILE` is reported as unsupported; set `GRACE_TOKEN` instead.
- OIDC M2M and CLI environment completeness is checked without token requests, browser/device-code flow, refresh,
  secure-store reads, or provider validation.
- `server.auth-principal.available` calls `/authenticate/me` only when a valid non-refreshing `GRACE_TOKEN` PAT is
  present.

Do not paste doctor JSON into public places without reviewing paths and environment names. The report is designed to
avoid raw secrets, but summaries may include local file paths or configured server URIs.

## Exact Local-State Repair

Use `grace doctor --repair-local-state` only when a materialized working tree remains intact but its local SQLite state
is missing or unusable. Repair:

- Refuses a live Watch process for the configured repository and branch.
- Computes the complete retained tree without changing its bytes.
- Resolves the real root identity and requires that exact root in the configured branch history.
- Uses the latest matching ordered event when the same root occurs more than once.
- Fetches the complete immutable server directory closure and preserves its real descendant identities.
- Rechecks configuration and local bytes immediately before one atomic status-and-cursor commit.

Repair never uploads files, creates directory versions, publishes Save or Reference events, or materializes server
content into the working tree. No exact root, changed bytes, a repository or branch mismatch, cancellation, server
failure, or SQLite failure leaves no partially initialized status. The default command never invokes this path.

### Planned Working Directory Update recovery

The Plan-ready [Working Directory Update specification](Working%20Directory%20Update.md) extends the explicit repair
gesture after the shared update module is implemented. A command that reaches `FinalizationIncomplete` will recommend
`grace doctor --repair-local-state`.

For a matching recorded update, the planned repair path first acquires the same local update lease, proves that working
bytes and local status still match the recorded target, and retries only the idempotent Branch or Watch finalization.
It performs no filesystem mutation. If recorded finalization is not applicable, Doctor may use the exact reconstruction
path described above. Changed or ambiguous bytes still cause refusal and preserve recovery evidence.

This section records an accepted design, not current executable behavior. Until the Working Directory Update work is
implemented, `--repair-local-state` retains the current exact reconstruction behavior above.

## V1 Boundaries

The current v1 command does not document or implement:

- Automatic repair, unmatched-root baselines, or implicit repair from Watch and other writable commands.
- Full/Aspire/cloud resource probes.
- Support bundle upload.
- Browser or device-code authentication flows inside doctor.
- Token refresh or secure-store mutation inside doctor.

These are explicit deferrals, not hidden current behavior.

The Issue #340 final audit below records the original read-only Doctor slice. Issue #823 supersedes only its repair
deferral: default diagnostics remain read-only, while `--repair-local-state` is an explicit exact-only maintenance
gesture.

## Final Audit

Issue #340 acceptance was audited against `Doctor.CLI.fs`, `CommandOutputContract.CLI.fs`, and the focused CLI tests.

| Acceptance item | Classification | Evidence |
| --- | --- | --- |
| Document command semantics, options, exit codes, check catalog, JSON examples, redaction, and repair deferral. | Implemented | This document covers those surfaces. |
| Keep doctor read-only and avoid command behavior changes. | Implemented | Existing tests cover no config/user-config/local-db/object-cache mutations. |
| Include stable check IDs, report version, and JSON DTO shape. | Implemented | `doctor-report-v1`, 27 catalog IDs, and `DoctorReportDto` are documented. |
| Refresh machine-readable CLI inventory evidence. | Implemented | `docs/Machine-readable CLI output.md` keeps registry-backed totals and doctor examples current. |
| Point README and authentication troubleshooting to doctor where useful. | Implemented | README and `docs/Authentication.md` contain short pointers. |
| Do not recommend repair or automatic repair commands. | Implemented | Repair is documented as deferred; maintenance commands are manual follow-ups only. |
| Do not document Full/Aspire/cloud probes as v1 behavior. | Implemented | V1 boundaries explicitly exclude those probes. |
| Cover docs examples against actual command behavior and JSON contract shape. | Implemented | Examples use `GraceReturnValue<DoctorReportDto>`, `--select` from `ReturnValue`, and current options. |
| Prove no raw secrets, unsupported cloud probes, stale counts, bad `--select` prefixes, or DB repair claims. | Implemented | Negative guidance is documented and backed by focused Doctor/CommandOutputContract tests. |
| Use current final validation, not stale sub-issue evidence. | Implemented | Validation for this slice is recorded in the PR handoff. |
| `--repair`, support bundles, cloud probes, and Aspire diagnostics. | Deferred | Explicit v1 boundaries; no current implementation. |
| Full production-release candidate review and merge state. | Out of scope | Owned by the orchestrator after this branch is pushed. |
| Hosted CI and a fresh review-subagent verdict on the final epic PR head. | Residual risk | Orchestrator-owned after PR creation/update. |
