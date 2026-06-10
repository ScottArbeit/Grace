# Branch Annotation Final Audit

Issue #322 closes the docs, ADR, and final-audit slice for branch annotation V1 under epic #313. This audit maps the
accepted R1-R12 design decisions to the live implementation, tests, and committed documentation on the current
`epic/313-branch-annotate-v1` integration branch.

## Audit Scope

- Parent epic: [#313](https://github.com/ScottArbeit/Grace/issues/313).
- Final audit issue: [#322](https://github.com/ScottArbeit/Grace/issues/322).
- Epic integration branch: `epic/313-branch-annotate-v1`.
- Final child branch: `agent/322-docs-adr-final-audit`.
- ADR: [ADR 0005](adr/0005-use-exact-path-reference-based-branch-annotation.md).
- User-facing docs: [Branch annotation](Branch%20annotation.md).

Status terms:

- `Implemented and proven`: live code exists and is covered by committed tests or route-contract evidence.
- `Documented`: committed docs or help text state the behavior.
- `Audited`: this final audit checked for the requested negative proof.

## Contract Summary

Branch annotation V1 explains visible text lines at one repository-relative path by mapping those lines to Grace
References in effective branch history.

The invariant tuple for this slice is satisfied by the current branch:

- Docs and ADR describe the exact-path Reference-based V1 contract and non-goals.
- CLI help exposes `grace branch annotate`, V1 options, exact-path scope, and the main non-goals.
- API and SDK surfaces annotate existing server-known References and do not save local workspace state.
- CLI implicit Save behavior is limited to the no-explicit-`--reference-id` path.
- DTO and JSON behavior expose spans, boundaries, source rows, and source References as arrays.

## R1-R12 Decision Traceability

| ID | Decision | Final status | Evidence |
| -- | -------- | ------------ | -------- |
| R1 | Branch annotation explains visible lines through Grace References. | Implemented and documented | `BranchAnnotationDto` and source Reference rows live in `src/Grace.Types/Annotation.Types.fs`. CLI help at `src/Grace.CLI/Command/Branch.CLI.fs:4510` describes Last changed and Introduced References. |
| R2 | V1 is exact-path only. | Implemented and documented | `AnnotationValidationRejectsCrossPathSourceRows` in `src/Grace.Types.Tests/Annotation.Types.Tests.fs:370` rejects cross-path source rows. CLI help at `src/Grace.CLI/Command/Branch.CLI.fs:4510` and ADR 0005 document exact-path scope. |
| R3 | Effective branch history includes authorized BasedOn relationships. | Implemented and proven | `buildEffectiveHistory` and BasedOn traversal live in `src/Grace.Server/Branch.Server.fs:145`. Unit tests cover ordering, unauthorized ancestors, missing parents, repeated links, traversal budget, and unreadable ancestors in `src/Grace.Server.Unit.Tests/AnnotationLineCore.Tests.fs:115`. Hosted child-rebase coverage is in `src/Grace.Server.Tests/Branch.Server.Tests.fs:679`. |
| R4 | The line model is deterministic. | Implemented and proven | `decodeVisibleText` and annotation line core live in `src/Grace.Shared/AnnotationLineCore.Shared.fs`. Tests reject invalid UTF-8, accept UTF-8 BOM, normalize line endings, and count whitespace changes in `src/Grace.Server.Unit.Tests/AnnotationLineCore.Tests.fs:299` and `src/Grace.Server.Unit.Tests/AnnotationLineCore.Tests.fs:319`. |
| R5 | Target content supports whole-file and manifest-backed text content. | Implemented and proven | `AnnotationMaterialization.Server.fs` materializes text content. Whole-file and manifest-backed tests are in `src/Grace.Server.Unit.Tests/AnnotationMaterialization.Server.Tests.fs:91` and `src/Grace.Server.Unit.Tests/AnnotationMaterialization.Server.Tests.fs:121`. |
| R6 | CLI current state is saved before annotate when needed and no explicit Target Reference is supplied. | Implemented and proven | `resolveAnnotationTargetReferenceWith` and annotate handler flow live in `src/Grace.CLI/Command/Branch.CLI.fs`. CLI handler tests map explicit options and target resolution in `src/Grace.CLI.Tests/Branch.CLI.Tests.fs:131`; this doc records that `--reference-id` suppresses implicit Save behavior. |
| R7 | Reference-type filter projects attribution only. | Implemented and proven | `AnnotateParameters.ReferenceTypes` is validated in `src/Grace.Shared/Parameters/Branch.Parameters.fs:118`. Projection and boundary tests live in `src/Grace.Server.Unit.Tests/AnnotationLineCore.Tests.fs`, including reference-type filtering and boundary-state coverage. |
| R8 | Boundaries are explicit and line-scoped. | Implemented and proven | `AnnotationBoundary`, `AnnotationSpan`, `SourceRows`, and validation live in `src/Grace.Types/Annotation.Types.fs:100` and `src/Grace.Types/Annotation.Types.fs:240`. DTO tests validate boundary/span links and requested-range containment in `src/Grace.Types.Tests/Annotation.Types.Tests.fs:213` and `src/Grace.Types.Tests/Annotation.Types.Tests.fs:329`. |
| R9 | `/branch/annotate` requires Branch read permission. | Implemented and proven | The endpoint authorization manifest includes `/branch/annotate` at `src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs:78`. `BranchAnnotateRequiresBranchReadAndPathRead` proves Branch read and path read requirements in `src/Grace.Authorization.Tests/EndpointAuthorizationManifest.Tests.fs:254`. |
| R10 | JSON output is one Grace result document. | Implemented and proven | The CLI uses `renderOutput` for annotate results. `json output remains a single Grace result document and skips human spans` is covered in `src/Grace.CLI.Tests/Branch.CLI.Tests.fs:371`. ADR 0004 remains the general CLI JSON contract. |
| R11 | V1 has no durable annotation-result cache. | Audited | Repository search found annotation DTOs, line-core helpers, materialization helpers, server route logic, CLI command logic, OpenAPI, generated docs, and tests. It did not find an annotation cache actor, setting, storage table, or cache lifetime for V1. `AnnotationDtoSerializationKeepsSourceReferencesAsArray` also proves no `AlgorithmVersion` field in `src/Grace.Types.Tests/Annotation.Types.Tests.fs:68`. |
| R12 | Docs and help list V1 non-goals and ADR captures the exact-path decision. | Implemented and documented | CLI help states the exact-path boundary and no rename, copy, or moved-block support in `src/Grace.CLI/Command/Branch.CLI.fs:4510`. ADR 0005 and [Branch annotation](Branch%20annotation.md) record the full V1 non-goal list. |

## API, SDK, CLI, And OpenAPI Alignment

| Surface | Current state | Evidence |
| ------- | ------------- | -------- |
| CLI command | `grace branch annotate` is routed with `--path`, `--reference-id`, `--line-range`, `--start-line`, `--end-line`, `--reference-types`, `--show`, and `--max-references`. | `src/Grace.CLI/Command/Branch.CLI.fs:4508`; parsing tests at `src/Grace.CLI.Tests/Branch.CLI.Parsing.Tests.fs:41`. |
| CLI JSON | JSON mode emits one Grace result document and ignores human `--show` shaping. | `src/Grace.CLI.Tests/Branch.CLI.Tests.fs:371`. |
| API route | `POST /branch/annotate` reads server-stored Reference content and does not save local workspace state. | `src/OpenAPI/Branch.Paths.OpenAPI.yaml:402` and `src/OpenAPI/Branch.Paths.OpenAPI.yaml:407`. |
| SDK | `Grace.SDK.Branch.Annotate` posts `AnnotateParameters` and returns `BranchAnnotationDto`. | `src/Grace.SDK/Branch.SDK.fs:31`; hosted route and SDK test at `src/Grace.Server.Tests/Branch.Server.Tests.fs:506`. |
| DTO | `BranchAnnotationDto` carries target metadata, lines, boundaries, spans, source rows, and source References. | `src/Grace.Types/Annotation.Types.fs:128`. |
| OpenAPI schema | `AnnotateParameters`, `BranchAnnotationApiDto`, and `BranchAnnotationReturnValue` are modeled. | `src/OpenAPI/Branch.Components.OpenAPI.yaml:132` and `src/OpenAPI/Branch.Components.OpenAPI.yaml:541`. |

## Negative Proof Audit

- No public docs added in this slice claim renamed-path following, copied-file detection, or moved-block detection.
- No public docs added in this slice claim API or SDK implicit Save behavior.
- No public docs added in this slice use alternative source-control attribution vocabulary for branch annotation.
- No durable annotation-result cache was found in source or configuration.
- ADR numbering was checked before writing: existing repo ADRs were `0001` through `0004`, so `0005` is the next ADR.
- `CONTEXT.md` already contains the branch annotation glossary and did not require correction for this slice.
- Nearby `AGENTS.md` files did not need durable guidance changes because no new workflow or agent behavior was added.

## Validation Plan For This Slice

The final worker validation for #322 should include:

- MarkdownLint for ADR 0005, this audit, and the branch annotation doc.
- CLI help/render checks for `grace branch annotate`.
- `pwsh ./scripts/validate.ps1 -Full` as the final build/test gate, because the epic includes server, storage, API, and
  SDK branch annotation behavior.
- `git diff --check <epic-target-ref>...HEAD`.
