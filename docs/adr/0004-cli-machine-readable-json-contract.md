---
status: accepted
date: 2026-06-05
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Charter Grace CLI machine-readable JSON output

Grace will make CLI machine-readable output a first-class public contract. `--output Json` must produce one stdout-clean
JSON document for every contracted command, keep human output away from stdout, and use the existing Grace result
envelope instead of creating a CLI-only wrapper.

## Context

Grace already offers JSON output as a CLI mode, but the current behavior is not yet a formal machine-readable contract.
The CLI has recursive `--output` option plumbing only. There is no accepted `--schema`, `--examples`, or `--select`
contract, and the current JSON path still needs implementation work before it is stdout-clean for every command.

The durable tracker source for this charter is [#274](https://github.com/ScottArbeit/Grace/issues/274), the parent
epic for CLI machine-readable JSON output. That issue records the external planning inputs, the accepted boundaries,
the child-issue DAG, and the inventory evidence used to create this ADR.

This ADR is self-contained for S1 and later implementers. Workers do not need local Downloads files, untracked HTML
artifacts, or any other non-repository planning artifact to understand the V1 contract or the inventory facts below.
The original direct inventory SHA-256 recorded in #274 is
`CA5FC683D2B6A83233808AEB3C492F6D6476CED4BD829EE15EFD41AC4E070996`.

The planning inventory was cross-checked before #274 was created. The direct JSON inventory matched the embedded report
matrix and was treated as the cleaner source for command-count and backlog detail.

The current inventory found `201` CLI leaf commands:

| Category | Count |
| -------- | ----: |
| Routed leaf commands | `192` |
| Unrouted or source-only leaf commands | `9` |
| Common `renderOutput` envelope path | `163` |
| Human/progress-only routed success behavior | `16` |
| Partial/manual routed success behavior | `6` |
| Manual non-enveloped JSON success behavior | `5` |
| Human/proc-only routed success behavior | `1` |
| Human-only routed success behavior | `1` |

The routed non-common backlog is `29` commands that need explicit DTO, renderer, envelope, or progress handling before
final all-command JSON audit closure. The `9` unrouted/source-only leaf commands are defined-only `reference` commands
and remain outside routed V1 coverage until the epic routes them, deletes them, or marks them source-only explicitly.

This ADR charters the accepted contract before implementation begins. Later slices must prove this contract through
registry coverage, stdout/stderr tests, schema/example generation, projection tests, and a final inventory audit.

## Decision

Grace CLI JSON mode will preserve the existing Grace result model. A successful command serializes the existing
`GraceReturnValue<'T>` shape. An error serializes the existing `GraceError` shape carried by `GraceResult<'T>`.

The CLI contract tuple is:

```text
(command, options, output mode, exit code, stdout document, stderr diagnostics)
```

For every contracted CLI leaf command:

- `--output Json` writes exactly one JSON document to stdout.
- Human-readable text, progress, tables, prompts, warnings, and diagnostics are written to stderr or suppressed.
- Stdout has no Spectre markup, banners, progress rows, success lines, trailers, or multiple JSON documents.
- Exit codes remain the process-level success or failure signal.
- Success and failure both use the existing Grace result vocabulary rather than a new CLI wrapper.

## Envelope Shape

Successful JSON output is the serialized `GraceReturnValue<'T>` envelope:

```json
{
  "ReturnValue": {},
  "EventTime": "2026-06-05T00:00:00Z",
  "CorrelationId": "correlation-id",
  "Properties": {}
}
```

Error JSON output is the serialized `GraceError` envelope:

```json
{
  "Exception": {},
  "Error": "error message",
  "EventTime": "2026-06-05T00:00:00Z",
  "CorrelationId": "correlation-id",
  "Properties": {}
}
```

The examples show the contract shape, not literal command output. Implementations must preserve the actual serializer
policy already used by Grace, including current field names and date/time serialization.

CLI-specific metadata belongs in `Properties`. Examples include output-contract version, command identity, selected
projection, schema/example provenance, or diagnostics that are safe to expose in machine-readable form. CLI metadata
must not create a second top-level envelope around `GraceReturnValue<'T>` or `GraceError`.

## Stdout And Stderr

JSON stdout is reserved for the single machine-readable document. This rule applies even when a command performs
multi-step work, reports progress, prints warnings, or fails after partial execution.

Commands that currently write human/progress output on success must be migrated before they can claim the JSON
contract. For JSON mode:

- Progress belongs on stderr when useful and safe.
- Human summaries belong on stderr or are suppressed.
- Diagnostic messages belong on stderr.
- Interactive prompts must not corrupt stdout.
- Commands that cannot run non-interactively must fail with a JSON error document on stdout and explanatory diagnostics
  on stderr when appropriate.

The final newline after the JSON document is allowed. Additional stdout content is not.

## Option Semantics

`--output Json` selects the machine-readable contract. Other output modes remain human-oriented unless a separate
contract says otherwise.

`--schema` is an inert introspection option. It must describe the JSON contract for the selected command without running
the command action, contacting the Grace Server, requiring repository state that the command would normally use, or
mutating local or remote state. It emits JSON regardless of `--output`.

`--examples` is an inert introspection option. It must print representative JSON examples for the selected command
without running the command action, contacting the Grace Server, requiring command target state, or mutating state. It
emits JSON regardless of `--output`.

`--select` applies only to `ReturnValue` in V1. It never projects `EventTime`, `CorrelationId`, `Properties`,
`Exception`, or `Error`. The V1 grammar is deliberately small:

- Dot-separated property paths over `ReturnValue`.
- Comma-separated paths when multiple fields are selected.
- Array/list item projection only when the implementation can keep the result shape deterministic.
- No predicates.
- No wildcards.
- No functions.
- No renaming.
- No computed fields.
- No metadata projection.

`--select` requires JSON mode except when it is used to inspect projected schemas or examples.

When `--select` cannot be applied, the command must fail with the normal JSON error envelope instead of producing an
ambiguous partial document.

## Schema And Examples Boundaries

Schema and example output exists to make the CLI contract inspectable before a command is executed. These paths are not
shortcuts for command execution.

Schema and example generation must be derived from the CLI output contract registry and the command's declared DTO
shape. They must not depend on live server data, remote discovery, local repository contents, command side effects, or
current user authorization.

Schema and example output can include contract metadata in `Properties`, such as the command identity, schema version,
or source registry entry. They must make unsupported or source-only commands explicit rather than guessing.

## Compatibility Rules

Grace treats this CLI JSON contract as a public compatibility surface once implemented.

Compatible changes:

- Adding nullable or optional fields to DTOs.
- Adding new safe keys in `Properties`.
- Adding new commands with declared contracts.
- Adding examples for already supported shapes.
- Expanding schema detail without changing the meaning of existing fields.

Potentially breaking changes:

- Renaming or removing existing envelope fields.
- Moving CLI metadata out of `Properties` into a new top-level wrapper.
- Emitting any non-JSON stdout in JSON mode.
- Changing a command's `ReturnValue` shape without compatibility handling.
- Changing `--select` behavior so a previously valid path returns a different meaning.
- Allowing schema or example paths to execute commands, contact the server, or mutate state.

Grace will prefer additive compatibility and explicit deprecation notes over silent contract changes.

## Accepted V2 Deferrals

The V1 contract intentionally defers:

- Predicate, wildcard, function, rename, computed-field, and metadata projection in `--select`.
- A CLI-only top-level envelope.
- Server-backed schema discovery.
- Live examples based on current repository or server state.
- Multi-document streaming JSON.
- JSON Lines progress streams.
- Stable schemas for commands that remain unrouted, source-only, or explicitly unsupported.

These deferrals are accepted boundaries, not accidental omissions.

## Rejected Alternatives

### Create a CLI-only top-level envelope

Grace will not wrap CLI JSON output in a separate top-level object such as `status`, `data`, `metadata`, and `errors`.
That would duplicate the existing `GraceReturnValue<'T>` / `GraceResult<'T>` model and create another compatibility
surface for users and SDKs to learn.

### Permit JSON plus human stdout

Grace will not treat stdout as a mixed stream in JSON mode. Tools that parse CLI output need one JSON document, not a
document plus progress rows, success text, banners, or markup.

### Make `--schema` and `--examples` execute commands

Grace will not make introspection depend on live command execution. Schema and example output must be safe to run in
automation, documentation, and offline planning contexts.

### Ship a broad V1 projection language

Grace will not begin with a query language for `--select`. V1 projection is limited to deterministic `ReturnValue`
property paths so implementation and compatibility can stay auditable.

## Consequences

Implementation slices after this charter need a command output contract registry, inert introspection plumbing, a
stdout-clean JSON writer, schema/example generation, V1 projection, command migrations, and final inventory audit.

Commands in the inventory that currently use manual JSON, human/progress output, partial/manual success behavior, or
source-only routing must be reconciled before the epic can claim complete CLI JSON contract coverage.

Documentation and pull requests for later slices should trace their evidence back to this ADR, #274, the recorded
inventory SHA-256, and the final inventory counts recorded when the epic closes.
