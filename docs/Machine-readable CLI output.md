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
```

bash / zsh:

```bash
grace --output Json authenticate logout
grace authenticate logout --schema
grace authenticate logout --examples
grace --output Json maintenance stats --select DirectoryCount
grace --output Json doctor --select Status
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
    "Schema": "FutureInertIntrospection",
    "Examples": "FutureInertIntrospection",
    "Select": "ExistingBehavior"
  }
}
```

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

- Total leaf commands: `209`
- JSON-ready routed commands: `185`
- Intentionally human-only commands: `1`
- Deferred routed commands with explicit V2 scope: `14`
- Source-only/unrouted commands: `9`
- Deleted commands: `0`

The intentionally human-only command is `watch`. In JSON mode, it short-circuits to a `GraceError` document instead of
starting the continuous foreground workflow.

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
- `cache.enroll`
- `cache.run`
- `cache.status`
- `diff.checkpoint`
- `diff.commit`
- `diff.directoryid`
- `diff.promotion`
- `diff.save`
- `diff.sha`
- `diff.tag`
- `history.run`

The `watch` command is not counted in those V2 routed-success migrations. It is intentionally human-only for success
behavior because it is a continuous foreground workflow; JSON mode returns an explicit error envelope instead of
starting the watcher.

`grace cache status` is a successful, pure observation command. Its `Lifecycle` value is `registered`,
`enrollment-recovery-required`, or `operator-recovery-required`; the latter is emitted exactly when persisted key
rotation recovery requires administrator revocation and re-enrollment. Status does not contact Grace Server, mutate
machine configuration, create or delete keys, start listeners, refresh, rotate, enroll, or expose secret values.

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
```

## V2 Deferrals

The following capabilities remain intentionally deferred beyond `cli-json-v1`:

- Predicate, wildcard, function, rename, computed-field, metadata, and streaming projections.
- JSON Lines progress streams and multi-document streaming JSON.
- Server-backed schema discovery.
- Live examples generated from repository or server state.
- Stable schemas for source-only/unrouted commands until they are routed, deleted, or promoted into a supported
  contract.

These are explicit V2 boundaries, not hidden V1 promises.
