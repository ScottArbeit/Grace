# Branch Annotation

`grace branch annotate` explains visible text lines at one repository-relative path by mapping those lines to Grace
References in the selected Branch's effective branch history.

Use branch annotation when you need to inspect how a line in a Branch reached its current visible content. V1 is an
exact-path feature: it explains the requested path only, using server-known Reference and content state.

## CLI Usage

PowerShell:

```powershell
grace branch annotate --path src/App.fs --start-line 12 --end-line 18
grace branch annotate --path src/App.fs --line-range 12,18 --reference-id c8f9bac8-d489-46c7-917f-b36b7d9efa9a
grace --output Json branch annotate --path src/App.fs --line-range 12,18 --reference-types Commit,Save
```

bash / zsh:

```bash
grace branch annotate --path src/App.fs --start-line 12 --end-line 18
grace branch annotate --path src/App.fs --line-range 12,18 --reference-id c8f9bac8-d489-46c7-917f-b36b7d9efa9a
grace --output Json branch annotate --path src/App.fs --line-range 12,18 --reference-types Commit,Save
```

Supported V1 options:

- `--path`: required repository-relative file path to annotate.
- `--reference-id`: optional Target Reference. Passing it suppresses CLI implicit Save behavior.
- `--line-range`, `-L`: optional inclusive `start,end` line range.
- `--start-line`: optional first 1-based line to annotate. Defaults to `1`.
- `--end-line`: optional final 1-based line. Defaults to `--start-line` when omitted.
- `--reference-types`: optional comma-separated attribution filter, such as `Commit,Save`.
- `--show`: human output source details, one of `last-changed`, `introduced`, or `both`.
- `--max-references`: maximum References to traverse. The default is `1000`; the server maximum is `5000`.
- Common Grace options, including `--output Json`, apply as usual.

## Target Reference Behavior

The Target Reference is the Reference whose directory and file state branch annotation explains.

When `--reference-id` is supplied, the CLI annotates that existing server-known Reference and does not create a Save.
When `--reference-id` is omitted, the CLI resolves the current Branch state. If local changes are present and Save is
enabled for the Branch, the CLI uses the existing Save workflow before it calls the annotation API.

The API and SDK do not save local workspace state. `POST /branch/annotate` and `Grace.SDK.Branch.Annotate` require an
existing server-known `TargetReferenceId`.

## API And SDK Contract

The HTTP route is:

```text
POST /branch/annotate
```

The SDK method is:

```text
Grace.SDK.Branch.Annotate(parameters: AnnotateParameters)
```

`AnnotateParameters` includes Branch identity, `TargetReferenceId`, `Path`, `StartLine`, `EndLine`,
`ReferenceTypes`, `MaxReferences`, and `IncludeLineText`.

The route requires Branch read permission and path read permission for the requested path.

## JSON Output

`--output Json` emits one Grace result document. Human spans, labels, and tables are not mixed into JSON stdout.

The `ReturnValue` is `BranchAnnotationDto`, which includes:

- `Class`: `BranchAnnotationDto`.
- `RequestedLineRange`: the inclusive target line range.
- `TargetReferenceId`: the server-known Reference being explained.
- `Path`: the repository-relative path.
- `ReferenceTypeFilter`: the attribution filter applied to source References.
- `MaxReferences`: the traversal budget.
- `IncludeLineText`: whether `Lines` contains visible target text.
- `Lines`: ordered target lines when line text was requested; otherwise empty.
- `Boundaries`: line-scoped incomplete proof.
- `Spans`: contiguous target-line ranges and their source-row or boundary links.
- `SourceRows`: source line ranges for annotation spans.
- `SourceReferences`: Reference metadata as an array.

`--show` affects human output only. JSON always includes the complete DTO for the selected request.

## Line And Path Semantics

Branch annotation V1 uses visible text lines:

- Lines are 1-based.
- CRLF and CR line endings are normalized to LF for comparison.
- A terminal newline does not create an extra visible line.
- Whitespace changes count as content changes.
- Text must decode as UTF-8; a UTF-8 byte-order mark is accepted.
- Target decoded text above 10 MiB is rejected.
- Whole-file content and manifest-backed text content are supported.

V1 is exact-path annotation. It explains the requested `Path` only.

## V1 Non-Goals

Branch annotation V1 does not:

- Follow renamed paths.
- Detect copied files.
- Detect moved blocks.
- Annotate binary files.
- Ignore whitespace.
- Annotate unsaved local state through the API or SDK.
- Create or depend on a durable annotation-result cache.

These boundaries are product decisions, not hidden gaps in the command syntax.

## Related Design Records

- [ADR 0005: Use exact-path Reference-based branch annotation](adr/0005-use-exact-path-reference-based-branch-annotation.md)
- [Branch annotation final audit](Branch%20annotation%20final%20audit.md)
