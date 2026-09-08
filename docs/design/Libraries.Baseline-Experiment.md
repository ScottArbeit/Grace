# Populated-Library baseline experiment

Result: **46 of 46 cases passed** on 2026-09-08. The selected sequence acquires complete immutable metadata before publishing files, within the existing three local table responsibilities. This was the preflight for [Issue #1071](https://github.com/ScottArbeit/Grace/issues/1071), selected after PR #1070 merged. It extends the accepted design at `e57998c4c56c22fffaa47d206831be7579ecb76b`; the earlier [storage experiment](Libraries.Storage-Experiment.md) remains historical evidence for the remote design.

## Environment and provenance

| Input | Recorded value |
| --- | --- |
| Source main | `68c421a52372e1e7484f98a2cf00ce9cc0e0562f` |
| Windows | `Microsoft Windows NT 10.0.26340.0`, NTFS |
| PowerShell | `7.6.5` |
| Native SQLite | `3.51.1`, WAL, foreign keys, FULL synchronization |
| Harness | `C:\Source\Grace-baseline-experiment-1037\Run.ps1` |
| Harness SHA-256 | `1B19AE55F815A62458E3658A40B01FEED8979C64A7C1B9B8A01714460BB2B8EF` |
| Raw results | `C:\Source\Grace-baseline-experiment-1037\results.json` |
| Raw results SHA-256 | `2D5F699866E2D0A8CD251A14A5ECE4993813C7B839182A125F9D29D4717289EC` |
| Run artifacts | `C:\Source\Grace-baseline-experiment-1037\run-20260908T1845291388038Z` |
| Case summary | [Machine-readable case results](Libraries.Baseline-Experiment.json) |

The external raw result retains full scenario state and 45 interruption snapshots. The co-delivered JSON retains every case name, result, effects, final state and snapshot location. Hashes identify the exact executed script and raw results. The external report and maintained report passed Markdown lint during Issue #1071.

## Selected sequence

Repository state holds bootstrap identity, epoch, catalog, boundary, continuation and phase. Operations hold selected baseline item work. Materialized items contain only verified live results. No selected item is represented as an invented accepted change, and no received page advances applied progress.

1. Require an empty ordinary local root and unchanged catalog. Persist selection and its first page atomically.
1. Persist each immutable page and its next token in one SQLite transaction. The final page marks metadata complete.
1. On an expired incomplete continuation, recheck empty root and catalog, then atomically discard only uninstalled work and select another baseline. Complete metadata retains its original selected revisions after token expiry.
1. Validate the complete live graph and install parents before children. Tombstones complete with no filesystem effect and no historical-parent lookup.
1. Persist item preparation. Stage and flush files, verify selected bytes, recheck catalog and local state, then publish. Reuse exact prepared publication residue after restart.
1. Verify the effect and commit the materialized live item with its terminal baseline operation. Leave the applied cursor absent.
1. Verify every required completed effect and the catalog. Commit the selected boundary separately, then pull genuine later accepted changes. Release saved-file capture after that catch-up completes.

An unprepared occupied target blocks even when its bytes match. Prepared empty directories can resume; unexpected children prevent completion. Changed completed files or parents prevent the selected boundary from becoming applied. Zero-byte local obstructions remain present and unapplied. Existing point-in-time filesystem checks and root exclusion remain the supported concurrency boundary.

## Executed cases

| Group | Cases | Result |
| --- | --- | --- |
| Unordered pages, tombstone and later remote edit | 1 | Child precedes both parents; retained selected revision installs before later accepted revision. |
| Interruption boundaries | 28 | Selection, page, preparation, staging, publication, item and boundary restart; page/item transaction failures roll back. |
| Incomplete versus complete acquisition expiry | 2 | Only incomplete acquisition restarts; complete metadata retains exact selection. |
| Catalog/file/zero-byte changes before publication | 3 | Effects stop, obstruction remains and boundary stays unapplied. |
| Catalog change after an installed file | 1 | Completed effect is retained; further effects stop. |
| Completed file changed before boundary | 1 | Changed bytes remain and boundary stays unapplied. |
| Watch positive control | 1 | A current saved nonempty edit reaches capture and submit callbacks. |
| Catalog change during acquisition | 1 | No effects or catalog adoption. |
| Interrupted expiry reset | 3 | Old or new transaction state remains complete. |
| New parent file or nonempty directory | 2 | Affected parent/child installation stops. |
| Prepared directory gains local child | 1 | Child remains and directory completion stops. |
| Materialized parent replaced by file | 1 | Descendant installation stops. |
| Expired token with changed catalog | 1 | Catalog stop precedes reset. |
| Total | 46 | All passed. |

Every successful final scenario invokes the client again and checks unchanged file timestamps and effect logs. Interruption cases require one publication per baseline file. Partial-installation cases observe no Watch capture or submission; the positive control demonstrates those callbacks can run after completion.

## Production coverage and limits

The experiment used real Windows filesystem operations and native SQLite transactions. Remote pages, retained content, catalog reads, expiry responses, accepted changes and Watch callbacks were modeled. Its content check used SHA-256 and length; production additionally checks BLAKE3. Simplified fixture JSON did not exercise the production F# serializer or Microsoft.Data.Sqlite adapter.

Issue #1071's `LibraryBaselineTests` exercises the production installer, serializer, real SQLite rollback and Windows publication. `LibraryLocalStateTests` retains accepted-change completion, saved-input and echo regression coverage. The hosted `LibrarySynchronizationWindowsServerTests` scenario uses actual CLI commands, immutable bootstrap pages, retained revision reads and accepted-change APIs for populated A and newly enabled B, including a later A edit and restart request/timestamp checks. These production tests remain separate evidence from the 46-case experiment.

Interruption means unwinding the invocation, discarding volatile client state and reopening SQLite. There was no forced process kill, power-loss test, live HTTP/manifest workflow, live SignalR lifetime or maximum-size performance measurement in this experiment. It does not cover general existing-file reconciliation, automatic rebaseline, disable/re-enable, catalog adoption, local namespace recognition, remote zero-byte content or other platforms. No new table, server owner or retention protocol was needed.

## Reproduction

PowerShell, on the recorded machine with the external harness:

```powershell
pwsh -NoProfile -File C:/Source/Grace-baseline-experiment-1037/Run.ps1
```

bash / zsh, invoking Windows PowerShell on the same machine:

```bash
pwsh -NoProfile -File C:/Source/Grace-baseline-experiment-1037/Run.ps1
```

The harness requires Windows and creates a new run directory beside itself. It is disposable experiment code, not a production client architecture or a repository dependency.
