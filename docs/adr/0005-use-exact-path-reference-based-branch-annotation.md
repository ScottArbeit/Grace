---
status: accepted
date: 2026-06-10
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use exact-path Reference-based branch annotation

Grace branch annotation V1 explains visible text lines at one repository-relative path by mapping those lines to
References in the selected Branch's effective branch history. The V1 contract is exact-path, Reference-based, and
intentionally narrow so every annotation result can be tested against Grace's stored Reference graph.

## Context

Grace already stores Branches, References, BasedOn links, and directory content as first-class domain objects. Users and
agents need a supported way to ask why visible lines at a path look the way they do without switching vocabulary or
leaving Grace's server-known Reference model.

The completed branch annotation design accepted these V1 boundaries:

- The command is `grace branch annotate`.
- The HTTP route is `POST /branch/annotate`.
- API and SDK callers annotate existing server-known References.
- The CLI may create a Save first when the working tree has local changes and no explicit `--reference-id` is supplied.
- Annotation follows effective branch history, including authorized BasedOn relationships.
- Line comparison is deterministic: visible text lines are 1-based, CRLF and CR are normalized to LF, and whitespace
  changes count.
- Results use annotation spans, line-scoped boundaries, source rows, and source References.

## Decision

Grace will implement branch annotation V1 as exact-path annotation at the requested `RelativePath`.

The Target Reference is the Reference whose directory and file state is being explained. API and SDK callers must supply
an existing server-known Target Reference through `AnnotateParameters.TargetReferenceId`. CLI callers can either pass
`--reference-id` explicitly or let `grace branch annotate` resolve the current Branch state. When the CLI resolves the
current state and finds local changes, it uses the existing Save workflow before calling the annotation API.

The annotation algorithm walks effective branch history through stored Reference relationships. A Reference-type filter
limits attribution only; it does not cut traversal. If the selected filter excludes an otherwise relevant Reference,
the result projects attribution to included Reference types or records an explicit boundary when proof cannot continue.

The result DTO is `BranchAnnotationDto`. It includes:

- `TargetReferenceId`, `Path`, `RequestedLineRange`, `ReferenceTypeFilter`, `MaxReferences`, and `IncludeLineText`.
- `Lines` only when line text was requested.
- `Boundaries` for line-scoped incomplete proof.
- `Spans` for contiguous target-line ranges.
- `SourceRows` for source line ranges.
- `SourceReferences` as an array of Reference metadata.

V1 does not persist annotation results in a durable annotation cache. Results are computed from stored Reference and
content state for the current request.

## Non-Goals

Branch annotation V1 does not:

- Follow renamed paths.
- Detect copied files.
- Detect moved blocks.
- Annotate binary files.
- Ignore whitespace.
- Annotate unsaved local state through the API or SDK.
- Create or depend on a durable annotation-result cache.
- Add broad provenance heuristics outside the exact-path Reference contract.

## Consequences

Documentation, CLI help, SDK comments, OpenAPI descriptions, and tests must describe one contract:

- `grace branch annotate` is the CLI entry point.
- `POST /branch/annotate` and `Grace.SDK.Branch.Annotate` work on existing server-known References.
- CLI implicit Save behavior exists only when no explicit `--reference-id` is supplied.
- API and SDK callers do not save local workspace state.
- V1 is exact-path only and does not make rename, copy, or moved-block claims.

Future versions may add broader path-history or movement-aware behavior, but those capabilities need their own product
decision, proof model, DTO/API compatibility review, and documentation update.
