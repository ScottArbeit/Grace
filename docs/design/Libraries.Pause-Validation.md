# Library pause and resume validation

[Issue #1075](https://github.com/ScottArbeit/Grace/issues/1075) implements LIB-016 through LIB-018 from the [accepted design](../Libraries.Design.md#pause-and-resume-accepted-next-slice). This is an unmerged candidate. Independent review, Shape Review and final-head GitHub Validate remain required.

The [56-case experiment](Libraries.Pause-Experiment.md) is preserved unchanged as readiness evidence. Its modeled remote and Watch entry calls are distinct from the production tests below. The supported world remains onboarded Windows copies under one unchanged catalog, with existing saved-content, rename and zero-byte protections.

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

Artifacts are retained under `C:/Source/Grace-artifacts/issue-1075/`; the PR records exact commands and the candidate revision. Local Fast/Full was not run. Independent review, Shape Review and final-head GitHub Validate are still pending. No power-loss, arbitrary process-termination, full offline Watch or other-platform guarantee is claimed.
