# Machine-Readable CLI Output

Grace CLI machine-readable output is the public automation contract for commands that opt into JSON mode. Use it when a
script, agent, CI job, or another tool needs parseable command output instead of human tables or progress text.

The contract version is `cli-json-v1`. The charter is recorded in
[ADR-0004](./adr/0004-cli-machine-readable-json-contract.md), and the live registry is implemented in
`src/Grace.CLI/CommandOutputContract.CLI.fs`.

## Quick Reference

- `--output Json` emits one JSON document to stdout for contracted commands.
- Successful JSON output uses the existing `GraceReturnValue<T>` envelope.
- Error JSON output uses the existing `GraceError` envelope.
- Human text, progress, prompts, and diagnostics must not be mixed into JSON stdout.
- `--schema` and `--examples` are inert introspection options. They do not run the selected command.
- `--select` projects fields from `ReturnValue` only. It does not read envelope metadata.

PowerShell:

```powershell
grace --output Json authenticate logout
grace authenticate logout --schema
grace authenticate logout --examples
grace --output Json maintenance stats --select DirectoryCount
grace --output Json doctor --select Status
grace --output Json watch --check
grace watch --check --select Mode
```

bash / zsh:

```bash
grace --output Json authenticate logout
grace authenticate logout --schema
grace authenticate logout --examples
grace --output Json maintenance stats --select DirectoryCount
grace --output Json doctor --select Status
grace --output Json watch --check
grace watch --check --select Mode
```

## Output Envelopes

A successful command emits a `GraceReturnValue<T>` document:

```json
{
  "ReturnValue": "Signed out.",
  "EventTime": "2026-06-05T00:00:00Z",
  "CorrelationId": "correlation-id",
  "Properties": [
    {
      "Key": "cli.contractVersion",
      "Value": "cli-json-v1"
    }
  ]
}
```

An error emits a `GraceError` document:

```json
{
  "Exception": null,
  "Error": "error message",
  "EventTime": "2026-06-05T00:00:00Z",
  "CorrelationId": "correlation-id",
  "Properties": [
    {
      "Key": "cli.contractVersion",
      "Value": "cli-json-v1"
    }
  ]
}
```

`--schema` and `--examples` emit registry-derived introspection documents. A schema response starts like this:

```json
{
  "Kind": "schema",
  "ContractVersion": "cli-json-v1",
  "Command": {
    "Id": "authenticate.logout",
    "Path": [
      "authenticate",
      "logout"
    ],
    "GroupPath": [
      "authenticate"
    ],
    "Name": "logout"
  },
  "Registry": {
    "RouteDisposition": "Routed",
    "CurrentJsonBehavior": "CommonRenderOutputEnvelope",
    "Category": "Mutating",
    "ExecutionScope": "LocalClient",
    "Mutating": true,
    "EnvelopeContract": "ExistingGraceResultEnvelope: ReuseExistingApiOrSdkDto",
    "JsonMode": "ExistingBehavior",
    "Schema": "ExistingBehavior",
    "Examples": "ExistingBehavior",
    "Select": "ExistingBehavior"
  }
}
```

For every routed command with an existing JSON success envelope, the registry stores its declared result type.
`--schema` derives the nested `ReturnValue` schema from that type and Grace's shared JSON options. `--examples`
constructs a deterministic representative value and serializes it with the same options. Both commands preserve the
outer `GraceReturnValue<T>` success envelope and the `GraceError` error envelope shown above.

The schema describes the JSON shape Grace already emits; it does not rename or remodel result types for presentation.
For example, `grace refs` is the alias for `grace branch get-references`, whose result remains
`BranchDto * ReferenceDto array`. Its `ReturnValue` schema and example are therefore two-element JSON arrays: the
branch object followed by the reference array.

## Select Projection

`--select` is intentionally small in V1. Selectors are relative to `ReturnValue`; do not prefix them with
`ReturnValue`.

Accepted examples:

- `--select DirectoryCount`
- `--select Summary.DirectoryCount`
- `--select Directories`

Rejected examples:

- `--select ReturnValue.DirectoryCount`
- `--select EventTime`
- `--select Properties`
- `--select Directories[0]`
- `--select Directories.*.RelativePath`
- `--select "Directories | length"`

Rejected selectors return a JSON error envelope. They do not produce partial output.

## Final Inventory Evidence

The final registry-backed inventory covers every CLI leaf command with exactly one disposition:

- Total leaf commands: `207`
- JSON-ready routed commands: `186`
- Conditionally JSON-ready routed commands: `1`
- Intentionally human-only commands: `0`
- Deferred routed commands with explicit V2 scope: `11`
- Source-only/unrouted commands: `9`
- Deleted commands: `0`

The conditional command is `watch`. `grace watch --check --output Json` returns a `WatchCheckStatusDto` in the normal
`GraceReturnValue<T>` envelope so scripts and agents can read `IsRunning`, `CanUseIncrementalStatus`, `Mode`,
`Reason`, and `SafetyFlags`. Foreground `grace watch --output Json` still short-circuits to a `GraceError` document
instead of starting the continuous workflow.

The source-only/unrouted commands are defined in source but are not attached to `GraceCommand.rootCommand` in V1:

- `reference.assign`
- `reference.checkpoint`
- `reference.commit`
- `reference.create-external`
- `reference.delete`
- `reference.get`
- `reference.promote`
- `reference.save`
- `reference.tag`

The deferred routed commands are not corrupt or unenveloped in current V1 output claims. They have explicit registry
metadata and V2 scope because their success paths still need migration before Grace can describe stable
`ReturnValue` schemas, examples, and projections for them. Current deferrals are:

- `branch.rebase`
- `branch.status`
- `branch.switch`
- `diff.checkpoint`
- `diff.commit`
- `diff.directoryid`
- `diff.promotion`
- `diff.save`
- `diff.sha`
- `diff.tag`
- `history.run`

The foreground `watch` workflow is not counted in those V2 routed-success migrations. Its supported machine-readable
surface is the `--check` status probe; starting the continuous watcher in JSON mode still returns an explicit error
envelope.

`doctor` is included in the JSON-ready routed count. It emits `DoctorReportDto` in the common Grace result envelope and
supports `--schema`, `--examples`, and `--select`.

## Agent Recipes

Use `--schema` first when an agent needs to understand a command contract without changing state:

```powershell
grace workitem show --schema
```

Then use `--examples` to get representative envelopes:

```powershell
grace workitem show --examples
```

For command execution, parse stdout as a single JSON document and treat the process exit code as the success or failure
signal:

```powershell
$json = grace --output Json maintenance stats
$document = $json | ConvertFrom-Json
$document.ReturnValue.DirectoryCount
```

For diagnostics, `doctor` returns a structured report:

```powershell
$json = grace --output Json doctor --check Authentication
$report = $json | ConvertFrom-Json
$report.ReturnValue.Status
$report.ReturnValue.Summary.Warning
```

When using `--select`, request only the `ReturnValue` field path the automation needs:

```powershell
grace --output Json maintenance stats --select DirectoryCount
grace --output Json doctor --select Status
grace watch --check --select Mode
```

## Branch Reference finalization retry output

Merged Issue #923 / PR #1009 records a typed Reference completion only after the working directory and SQLite status are
verified. Issue #871 consumes that pending row during a later `branch switch` invocation before the legacy switch
pipeline begins. The retry has a small JSON projection so automation receives the same outcome, message, and exit class
as human output.

| Field | Type | Meaning |
| --- | --- | --- |
| `Outcome` | string | `Updated`, `Unchanged`, `Rejected`, `UpdateIncomplete`, or `FinalizationIncomplete`. |
| `Message` | string | Empty for success; otherwise the human repair guidance. |
| `BranchId` | GUID | The durable selected Branch when it can be reread; otherwise the invocation's last readable Branch identity. |

Example after a restart finds a selected Branch and terminalizes its matching pending row:

```json
{
  "Outcome": "Updated",
  "Message": "",
  "BranchId": "5b2653ac-0d57-45da-8f1a-e5a3f8131a40"
}
```

`Updated` and `Unchanged` return exit code `0`. `Rejected`, `UpdateIncomplete`, and `FinalizationIncomplete` return a
nonzero exit code. `FinalizationIncomplete` keeps the pending row and includes `grace doctor --repair-local-state` in
`Message`. It does not rewrite working files or republish an already selected Branch.

This limited retry projection does not remove `branch.switch` from the broader V2 routed-success deferral list. Connect,
Watch, and Doctor implementation remain deferred.

## V2 Deferrals

The following capabilities remain intentionally deferred beyond `cli-json-v1`:

- Predicate, wildcard, function, rename, computed-field, metadata, and streaming projections.
- JSON Lines progress streams and multi-document streaming JSON.
- Server-backed schema discovery.
- Live examples generated from repository or server state.
- Stable schemas for source-only/unrouted commands until they are routed, deleted, or promoted into a supported
  contract.

These are explicit V2 boundaries, not hidden V1 promises.
