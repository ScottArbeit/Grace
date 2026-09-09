# Library pause/resume experiment

Question: can durable pause and explicit resume use the existing repository row and working-root lease while retaining every admitted operation family, without another queue, table, database or recovery owner?

**Verdict: proven within the bounded model. All 56 cases passed.** The [accepted behavior](../Libraries.Design.md#pause-and-resume-accepted-next-slice) is Plan-ready for one Product V1 implementation slice, LIB-016 through LIB-018. No pause/resume production command is delivered by this experiment.

The [owner decision and readiness record](https://github.com/ScottArbeit/Grace/issues/1037#issuecomment-5597448981) captures the five accepted choices. Source base is `66aea34838a65a9e4b220463f0d8366836ee328d`, freshly fetched after PR #1074. The intervening main changes are unrelated Operations work; Library source and documents match the delivered rename/type/object behavior.

## Actual execution and artifacts

The [program](Libraries.Pause-Experiment/Program.fs), [console project](Libraries.Pause-Experiment/PauseExperiment.fsproj), and [PowerShell runner](Libraries.Pause-Experiment/Run.ps1) are reproducible disposable evidence. The runner itself executed successfully, including a fresh copied project build, on 2026-09-09 from `07:01:13.9653803Z` to `07:01:24.1836638Z`. Build: zero warnings and errors. [Machine-readable results](Libraries.Pause-Experiment.json) contain every named case and the source/script hashes.

| Artifact | SHA-256 |
| --- | --- |
| `Program.fs` | `2BBD9558EA7BADB2C2D74715DD94D9131788D24C75C17B10842C55DED1AF305C` |
| `Run.ps1` | `216610858D1436E317629987818769807E522577DA092728E7385C1DCF62B043` |
| Recorded result JSON | `9A6773D22FE46346B6067E9ACE83B1D4060359ADC7673467D2D29D7E2BBA341D` |

Local run artifacts remain at `C:/Source/Grace-artifacts/library-pause-1037/final-20260909/`: build/run logs, copied source/project, real scratch databases and files, 25 directly captured interruption snapshots and 33 restart snapshots. The before/inside/after transaction cases inspect fresh persisted state after rollback or commit. An inside-transaction exception is captured after disposal, so its restart snapshot describes rolled-back durable state. Earlier runs and compilation diagnostics remain separate; the final wrapper run is the recorded result.

PowerShell:

```powershell
./docs/design/Libraries.Pause-Experiment/Run.ps1 -SourceRoot C:/Source/Grace-worktrees/issue-1037-library-pause-readiness
```

bash / zsh:

```bash
pwsh ./docs/design/Libraries.Pause-Experiment/Run.ps1 -SourceRoot C:/Source/Grace-worktrees/issue-1037-library-pause-readiness
```

Both commands require Windows, PowerShell 7.6, the repository-selected .NET SDK and existing restore access. Use a source checkout matching the pinned base outside documentation; the runner rejects changed production inputs and uncommitted source changes. It creates a unique scratch output directory, copies only the experiment project/program, builds through the existing CLI project reference and leaves results for inspection. It does not create/remove branches or worktrees. The build can create ignored `bin`/`obj` outputs. The console assembly uses the existing `Grace.CLI.Tests` friend boundary to exercise internal production dependencies; it is not a test-runner project or part of the product solution.

## Real dependencies and explicit models

| Dependency | Evidence actually exercised |
| --- | --- |
| Windows filesystem | Real nonempty and zero-byte files, immutable-object fixture files, destination publication/source removal, unchanged timestamps on completed restart, and exclusive Windows handles. |
| Root exclusion | Actual `WorkingDirectoryUpdateCoordination.Scope` and cancellable `Lease.acquire`. A second caller waits on the held lease; cancellation does not mutate participation. A separately scheduled stale reader uses deterministic events, without arbitrary test sleeps. |
| SQLite | Actual `LibraryLocalState.initialize` and `openConnection`, including WAL/FULL settings; three existing Library tables; real transactions and rollback. The prototype adds one constrained `paused` column to its scratch repository row. This `ALTER TABLE` is fixture setup, not a proposed production migration. |
| Typed persistence | Actual `LibraryOperation` families, Grace serializer, `insertOperation`, `readOperations` and derived-index checks, exact `updateOperation`, and real `completeWith` transaction for item/operation/cursor. Existing repository progress writes leave the prototype's independent pause column intact. |
| Objects and hashes | Actual typed `SavedObject`/`ContentIdentity` and production dual-hash helper. Fixture object publication/verification uses real files. It does not rerun the production streaming capture/uploader. Prior accepted object evidence applies to those unchanged algorithms. |
| Server | Explicit disk-persisted model of accepted receipt and current catalog/epoch/rebaseline response. Requests are labeled model strings within real operation JSON; no HTTP, real manifest upload, authorization or server conflict allocator is exercised. The lost-response case reuses one modeled acceptance. |
| Watch and command routing | Named entry calls pass through the proposed under-lease guard with negative and positive controls. These are not actual CLI commands, filesystem callbacks or a running Watch/SignalR process. The source mapping below makes those production obligations explicit. |

No application progress is recovered from diagnostic logs or snapshots. Each invocation rereads the real rows/files. The copy handle carries only working-root, database path and repository identity; it does not retain operation or progress objects across restart.

## Selected algorithm

Keep one independent pause Boolean on the existing repository row. Keep current progress, catalog, materialized items, operation JSON, pending object references and echo state intact. The experiment does not reset or remove participation.

1. Pause acquires the existing root lease with cancellation. Reread the participant and require completed onboarding. Persist only the pause value in a transaction, then report success. The active holder may complete work first; pending work is not drained as a condition of pause.
1. An interrupted pre-commit or rolled-back update retains the old setting. An interrupted post-commit response retains the new setting. Retry reads the row and is idempotent; no durable pause-command operation or receipt is needed.
1. Every Library effect entry rereads pause under the lease. Cached status, Watch wake delivery and an earlier explicit resume are insufficient. Paused capture, transfer, incoming application and echo retirement perform no effects. Catalog ownership and version-control exclusion remain.
1. Explicit resume commits active participation before attempting the finite run. A failure retains active/blocked behavior and saved work. If another pause commits between resume and run, the run's fresh under-lease read stops it; the earlier resume never clears that newer pause.
1. Resume retains frozen operations and captures only the latest supported current disk bytes against the original materialized base. Then the existing submission/ordered-application sequence owns receipt recovery, filesystem effects and atomic item/operation/cursor completion. Recheck current catalog/feed availability and existing exact-operation/filesystem conditions before affected effects. A catalog/rebaseline stop preserves state.

One pause setting and existing work are sufficient for these cases. No new table, server owner, generic workflow, scheduler, retention lifecycle or conflict rule was needed. The production `RepositoryState` type must explicitly represent pause and include it in current reads/writes/comparisons; the prototype's side-by-side column does not establish those future adapters automatically.

## Finite case ledger

| Cases | Count | Assertions and implications |
| --- | --- | --- |
| `current`, `saved`, `rename` × pause/resume × before/inside/after commit | 18 | Restart observes only the committed setting. Repository progress, actual typed operation JSON/indexes and object bytes remain unchanged. Duplicate commands are idempotent. Saved starts uploaded with an unresolved modeled response; rename starts accepted/prepared with its destination already published. |
| Stale `run`, `enable`, `rename`, `capture`, `timer`, `observation`, `wake`, `echo-prune` | 8 | Every modeled entry rechecks pause under lease despite its earlier active observation. No effect while paused; each positive control executes after resume. These labels do not establish production call-site integration. |
| Lease cancellation and active holder completion | 2 | Pause really waits. Canceling the waiter leaves participation active. Work written by the active holder survives successful later pause. |
| Resumed saved-submission boundaries | 8 | Before submit, after modeled server acceptance, after receipt, before/after publication, before/inside/after real completion. Restart uses the same operation and modeled acceptance; cursor stays at its predecessor until completion. A subsequent completed run does not rewrite the file. |
| Resumed partial-rename boundaries | 7 | Before/after publication, before/after source removal, before/inside/after real completion. An already published destination is reused; restart completes stable item identity without rewriting completed content. |
| Network, changed catalog, rebaseline response | 3 | Explicit resume remains active/blocked, with exact pending work and unchanged applied cursor. Repeated attempts do not bypass the stop. Catalog/feed availability are modeled responses read from disk. |
| Latest saved bytes with newer remote metadata | 1 | Several paused edits create no objects/operations. Resume captures only latest bytes and retains the original materialized content revision despite newer modeled remote metadata. This case does not apply that remote change or run the server conflict allocator. |
| Later pause wins; concurrent stale reader | 2 | A resumed run respects a later pause. A separately scheduled caller that read active before the pause cannot produce an effect after acquiring the lease. |
| Zero-byte obstruction, missing object, corrupt object, rejected saved work | 4 | Empty local file remains, frozen positive object remains when available, damaged references stop before modeled submission, rejected saved work stays pending, and no newer working bytes replace frozen input. |
| All operation families, incomplete onboarding rejection | 2 | Actual JSON/index round trips retain saved, directory, rename, incoming, terminal live baseline and terminal tombstone work. Exactly three Library tables remain. Incomplete onboarding cannot be paused through this slice. |
| Two copies, same modeled accepted rename | 1 | Both copies have the same repository/catalog/item. A remains paused while B applies the modeled accepted change; A resumes to matching bytes/item and both restart without completed-file rewrites. |

Total: **56 passed, 0 failed**. The final program and wrapper hashes above identify the executed version.

## Production mapping and readiness consequence

| Requirement | Current source seam | Required production acceptance |
| --- | --- | --- |
| LIB-016: durable explicit pause and fresh gating | `LibraryLocalState.RepositoryState`, schema/read/update/completion paths; `LibrarySynchronization.enableParticipation`, `run`, `rename`, capture/classification; `Watch.CLI` timer | New commands and status through real CLI processes; actual Watch already running before pause; stale pre-lock status, canceled wait, resume/later-pause race and process restart. Verify paused paths preserve version-control exclusion and produce no Library effects. |
| LIB-017: retain all selected work | `LibraryOperation`, existing saved capture/object reader/uploader, local operations, Windows application and echo handling | Actual captured frozen request survives pause, response loss and restart; real uploader uses original object; partial rename and saves at either path remain protected. Exercise a later saved edit with a compatible remote content change using actual server acceptance, plus zero-byte and damaged-object negatives. |
| LIB-018: active resume failures and truthful status | Status/output registry and command handler; existing catalog/changes/rebaseline checks | Human and JSON `Paused` plus unchanged `Enabled`/progress fields and schema; failed resume remains active/blocked; ordinary retry after a recoverable failure; changed catalog/rebaseline does not reset or overwrite a participating copy. Actual A/B convergence and restart show no duplicate uploads or rewrites. |

The [maintained design](../Libraries.Design.md#pause-and-resume-accepted-next-slice) and [type plan](Libraries.Type-Plan.md#pauseresume-propagation-accepted-not-implemented) contain the accepted commands, state/effect ordering, stable requirements, affected surfaces and stop conditions. Use a fresh mainline base and one Tier 2 implementation owner after issue readiness and a new run charter. No enabling production PR or integration branch is justified by this result.

## Limits

This is a disposable algorithm experiment, not a delivered command or an implementation review. It simulates interruption by discarding volatile state over real files and SQLite; it does not certify arbitrary process termination or power loss. The synchronous file publication/removal and fixture object handling are finite models using real effects, not replacements for the accepted production filesystem and streaming capture algorithms.

The experiment does not prove actual upload idempotency, production HTTP/CLI pause behavior, runtime authorization, full remote edit/conflict composition or every Watch/SignalR lifetime. Those remain the focused production acceptance cases above. Source-root/catalog identity, operation and cursor retention are real local checks; remote decisions are explicitly modeled. It does not add offline Watch recovery, rebaseline, catalog adoption, new namespace gestures or platforms.

No product build/test gate, R1, Shape Review or GitHub Validate pass is claimed for a pause/resume implementation. The console and its unchanged production dependencies built to execute the experiment. The 49-case rename, 18-case saved-object and PR #1074 hosted evidence remain separate and unchanged.
