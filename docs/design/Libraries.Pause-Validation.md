# Library pause and resume validation

[Issue #1075](https://github.com/ScottArbeit/Grace/issues/1075) implements LIB-016 through LIB-018 from the [accepted design](../Libraries.Design.md#pause-and-resume-accepted-next-slice). [PR #1076](https://github.com/ScottArbeit/Grace/pull/1076) records the delivery state, independent review, Shape Review and final-head GitHub Validate.

The [56-case experiment](Libraries.Pause-Experiment.md) is preserved unchanged as historical readiness evidence. Its modeled remote and Watch entry calls are distinct from the production tests below. Reproduce it with the source dependencies pinned by that report: its string-epoch fixtures are not compatible with the later GUID-backed epoch type. Its effect-order evidence remains relevant, but no current-source reproduction is claimed. The supported world remains onboarded Windows copies under one unchanged catalog, with existing saved-content, rename and zero-byte protections.

## Requirement mapping

| Requirement | Production evidence |
| --- | --- |
| LIB-016: durable pause, lease reread, canceled wait, no paused capture/classification | `pause preserves progress and exact work while stale callers cannot complete`; `pause cancellation and competing stale Watch run honor root lease` in `LibraryLocalState.Tests.fs`. |
| LIB-016: running Watch races CLI pause/resume while B continues | `live Watch and CLI pause retain local saves while another copy continues` in the hosted Windows fixture. Positive Watch capture precedes pause; paused saves, direct run/enable/rename and incoming content are checked before explicit resume. |
| LIB-017: frozen saved input, missing/corrupt objects | `committed saved object resumes without caller identity and uploads frozen content`, all three cases. Pause preserves exact rows, object references and upload counts; failed resume remains active. |
| LIB-017: lost response and later saved input | `two Windows copies converge exact Library bytes and restart current`; `saved create successor and stale edits preserve exact causal content`. Pause follows lost acceptance, then explicit resume uses retained requests and sources. |
| LIB-017: partial rename and later saves at either path | `accepted saved edit and later partial rename save survive process restart`, both cases, now pauses before the later save and resumes through the actual command. |
| LIB-017: rejected work and zero-byte obstruction | `deletion first retains ItemTombstoned saved edit without resurrection`; `incoming changes preserve excluded zero files`, all four cases. Explicit resume retains rejected or obstructed state. |
| LIB-018: active/blocked on changed remote boundary | `resume stops active with retained state when remote participation boundary changes`, catalog/epoch/rebaseline cases. Catalog changes use the real server; epoch/rebaseline responses are injected by the HTTP proxy. |
| LIB-018: participation, status and command contract | `pause requires completed participation and constrained SQLite value`; `library synchronization accepts exact verbs and repository locators`; `library synchronization schema exposes independent pause setting`. |
| LIB-018: completed baseline history permits toggles | `populated A and new B install selected baseline then later accepted edit without duplicate publication on restart`, followed by pause and resume after catch-up completes. |

## Scope and validation status

Pause adds one constrained Boolean to the existing repository row and one field to status. It creates no operation type, table, database, server contract or WDU completion. Completed baseline history is retained; the repository's unfinished baseline selection still rejects toggles.

Focused local validation passed on Windows:

- CLI/local SQLite/parser/output contract selection: **127 passed, 0 failed, 0 skipped** (`cli-library-current.trx`).
- Hosted Library selection: **34 passed** with the live Watch case initially failing in its test setup (`hosted-library-final.trx`). The unchanged production code then passed the corrected live Watch case separately: **1 passed, 0 failed, 0 skipped** (`hosted-watch-auth.trx`). Together these establish all 35 selected hosted cases. The correction seeds an actual VC Reference for Connect and supplies the same real PAT and direct server URI to Watch and its companion CLI processes.
- Matching Release builds for both test projects completed with zero warnings and errors. The hosted rerun rebuilt after the test-only correction. Touched F# formatting, Markdown lint with MD013 ignored, PowerShell parsing, readiness artifact hashes and `git diff --check` passed.

The live Watch case proves initial capture, paused local saves and incoming isolation while B continues, resumed latest saved bytes with a compatible remote rename, restart without rewriting completed files, and unchanged VC Reference state. Earlier failed runs remain in the local evidence directory rather than being reported as passing proof.

Artifacts are retained under `C:/Source/Grace-artifacts/issue-1075/`; the PR records exact commands, the candidate revision and review outcomes. Local Fast/Full was not run. Final-head GitHub Validate remains required. No power-loss, arbitrary process-termination, full offline Watch or other-platform guarantee is claimed.

## Main composition

The [composition charter](https://github.com/ScottArbeit/Grace/issues/1075#issuecomment-5598766821) updates the delivery base to `8b418ac04e154b129809f673a2be2eecc4c6558e`. The merge preserves the Library candidate `d0503c5b585eaea873d628a7599aac401fd68a2d` and the landed Owner observation command. The three conflicts were limited to inventory counts and adjacent CLI guidance.

After a fresh CLI restore and matching Release build, the combined Library/output/Owner client selection passed **138 executed tests with no failures** (`composition-cli.trx`). One existing Owner entry-point test is excluded on Windows because its temporary-profile isolation requires Linux; it remains a CI check. The command-tree and registry checks confirm 218 total entries, 209 routed entries, 197 JSON-ready entries and 198 schema-eligible entries.

At that composition revision, Library production, Watch, hosted Library tests, Library SDK/types/validation and the root lease were identical to `d0503c5b`; the 34-plus-1 hosted evidence above retained its applicability. All 74 incoming paths outside the five overlapping documentation/registry paths matched the new main exactly. `composition-preservation.json` records the comparisons. The integrated build had zero warnings and errors. No additional hosted or algorithm run was needed for those additive command-registration changes.

## CI platform correction

[Validate run 34329679911](https://github.com/ScottArbeit/Grace/actions/runs/34329679911) found three local pause tests calling the Windows-only participation API on Linux. Those tests now use the existing Windows filesystem guard, preserving every assertion. The cross-platform SQLite participation/constraint tests remain enabled. Production code and hosted evidence inputs are unchanged. The corrected revision still requires a fresh GitHub Validate result.

A matching Release build passed with zero warnings and errors, followed by all five local pause cases on Windows: **5 passed, 0 failed, 0 skipped** (`platform-guard-build.log`, `platform-guard.trx`).

## Status type naming

Owner review renamed the local result type from `Status` to `LibrarySynchronizationStatus`. The record fields, JSON payload shape and synchronization behavior are unchanged. Schema introspection changes only `ReturnValueContract` and the success-envelope/ReturnValue schema titles to reflect the descriptive name.

The matching Release build passed with zero warnings and errors; all **33 output-contract tests passed** (`status-rename-build.log`, `status-rename.trx`). Before/after status-schema captures confirm those three metadata changes only. Existing runtime evidence remains applicable.

## GUID epoch refinement

The [accepted epoch charter](https://github.com/ScottArbeit/Grace/issues/1075#issuecomment-5607772879) uses main `e42b7c50e007a7ed3dbe6be45a5ce7938398e979` and preserved composition `b60b4c3b`. `LibraryCursorEpoch` is a distinct GUID-backed struct union with equality and no ordering. JSON and SQLite use hyphenated GUID D text; `Guid.Empty` remains valid. Applied cursors and participation/progress representations are unchanged.

The global F# decoder ordinarily converts a null GUID field to its default value. The [scope clarification](https://github.com/ScottArbeit/Grace/issues/1075#issuecomment-5607922429) therefore adds an epoch-only check for the four remote DTO/event types in `Constants.Shared.fs`. This file compiles before Library types, so its factory uses four exact CLR names. It validates a reader copy and delegates the original stream directly to the existing F# converter. Nested records retain the registered guard; other fields retain their null policy, and optional disabled-status epochs remain valid.

| Boundary | Focused evidence |
| --- | --- |
| JSON D-format, valid empty GUID, malformed scalar and DTO input | `epoch JSON uses canonical GUID D text and round trips`; `epoch JSON rejects malformed and non D values`. |
| All four remote DTO/event types and Orleans codecs | `PopulatedFinalLibraryRpcGraphRoundTripsThroughProductionOrleansSerialization` checks actual JSON and Orleans round trips, null/missing/malformed epochs, nested rebaseline rejection, case-insensitive duplicate-property behavior, and unchanged unrelated null fields. |
| SQLite round trip and rejected identity retention | `SQLite epoch boundary rejects malformed text and retains pending rows`; existing pause, baseline and stale-completion tests use typed epochs. |
| Disabled status and public schema | `pause requires completed participation and constrained SQLite value` round-trips absent status epochs; all five synchronization schema cases require UUID format. |
| Hosted HTTP and running Watch | The Windows Library fixture retains the complete pause/resume coverage and adds `malformedEpoch` to the retained-state resume stop cases. |
| Generated contracts | The maintained OpenAPI component, bundled projections and SDK generator matrix expose UUID epochs; freshness and TypeScript/Python/Rust generator probes validate propagation. |

The PR records exact-revision build/test results, hosted Windows execution and final GitHub Validate. Local artifacts use the `epoch-` prefix under the evidence directory above. Historical effect-order evidence remains relevant, but epoch serialization and boundary acceptance require this revision's focused proof.
