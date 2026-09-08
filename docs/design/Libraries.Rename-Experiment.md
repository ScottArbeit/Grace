# Explicit Library file rename experiment

Verdict: **Proven within the selected model and supported Windows filesystem/SQLite scope. All 49 cases passed.** The explicit same-parent rename design is Plan-ready for one implementation slice. This result does not claim that the command or its production tests have been delivered.

The question was whether one clean synchronized nonempty file can acquire a durable outgoing rename intent, receive a genuine server decision, apply accepted changes in order and recover after interruption within the existing three local table responsibilities. Scott accepted the explicit command and rejection/resume recommendations in the [decision record](https://github.com/ScottArbeit/Grace/issues/1037#issuecomment-5591944045), following the [post-PR #1072 checkpoint](https://github.com/ScottArbeit/Grace/issues/1037#issuecomment-5591691687).

## Environment and reproducibility

| Input | Recorded value |
| --- | --- |
| Source main | `8ce7cd4dda7089e801662dfc410bab4dcbf0ff81`, freshly fetched on 2026-09-08 |
| Windows | `Microsoft Windows NT 10.0.26340.0`, NTFS |
| PowerShell | `7.6.5` |
| SQLite | Native Windows `winsqlite3.dll`, version `3.51.1`; WAL, FULL synchronization, foreign keys |
| Executed script | `C:/Source/Grace-rename-experiment-1037/Run.ps1` |
| Script SHA-256 | `A786B0B95FEC40F4C87FA61F0685F4E5541CF3E8004E30F0FD0F1CCC07D7E05E` |
| Native adapter SHA-256 | `1A3E23EC32C8DFB1D1A74AA89E21BB1DDBA5D47E569A443FD578E23D01C56D5D` |
| Raw result | `C:/Source/Grace-rename-experiment-1037/results.json` |
| Raw result SHA-256 | `ADA92C9E65BB0756AB3E338BC8FC91B09BB7C3D6596CC86A19CEFC42E87DC854` |
| Captured run | `C:/Source/Grace-rename-experiment-1037/run-20260908T2137213831629Z` |
| Recorded interruptions | 28 snapshots with database rows and physical file fingerprints/timestamps |
| Versioned artifacts | [Script](Libraries.Rename-Experiment/Run.ps1), [native adapter](Libraries.Rename-Experiment/Sqlite.ps1), [case/effect summary](Libraries.Rename-Experiment.json) |

The hashes identify the executed scratch files before any checkout line-ending conversion. Earlier development runs remain in separate scratch run directories; the table identifies the corrected 49-case run. Recovery reads database rows and filesystem residue, never the evidence logs or snapshots.

To rerun, copy the two scripts to a new scratch directory and execute on Windows. Each invocation creates its own run directory. Do not run inside a real Library root. This example copies only the two named scripts and does not delete any existing directory.

PowerShell:

```powershell
$renameExperiment = Join-Path $env:TEMP ('grace-rename-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $renameExperiment | Out-Null
Copy-Item -LiteralPath './docs/design/Libraries.Rename-Experiment/Run.ps1', './docs/design/Libraries.Rename-Experiment/Sqlite.ps1' -Destination $renameExperiment
pwsh -NoProfile -File (Join-Path $renameExperiment 'Run.ps1')
```

A bash/zsh execution variant is N/A: this experiment requires Windows native SQLite and Windows filesystem behavior.

## Selected states and effects

Each of A and B has a real SQLite database containing only `library_repository_state`, `library_items` and `library_operations` as Library tables. Repository state records catalog and applied progress. Items describe completed materialization. The existing operation responsibility holds the selected intent, frozen request, server receipt, effect preparation, accepted saved bytes and terminal/echo classification. SQLite's internal autoincrement sequence is not another Library responsibility.

The modeled server persists accepted item snapshots, ordered changes and immutable receipts in a separately labeled `server-model.json`. It preserves stable item identity and content revision for rename, checks only the existing namespace version and destination occupancy, and replays an exact operation receipt before changed preconditions. Only this explicit server model creates accepted changes. Local selection or receipt persistence never manufactures accepted progress.

| Step | Durable or physical effect | Interruption residue and next action |
| --- | --- | --- |
| Admit | Root lease; ordinary clean source, absent destination, current state/catalog; persist generated intent identity | Before commit there is no intent. After commit resume that intent; a different target cannot replace it. |
| Freeze | Persist exact request under the same operation identity | Resume from that request; cancellation does not erase or replace it. |
| Submit and retain receipt | Modeled permanent server decision, then local receipt | Response loss replays the exact request/receipt. An accepted receipt alone leaves applied progress at its predecessor. |
| Definitive rejection | Exact operation transaction sets terminal/rejection and no echo, with no item or cursor update | Before/inside failure leaves pending rejection classification; after commit unrelated synchronization can proceed. Only an unprepared rejected rename can take this path. |
| Ordered pull and prepare | Read every actual predecessor; persist accepted change, original materialized base, target and expected filesystem evidence | Resume preparation from the row. Compatible remote edits before the rename apply first, even when the rename receipt already contains their resulting bytes. |
| Stage and publish | Real staged write, flush and content check; catalog/path checks; destination publication | Stage residue can be replaced. Exact prepared destination bytes are reused without another publication or timestamp change. |
| Remove source | Recheck source and saved-byte acceptance, then remove old path | Destination may exist while source remains. Changed positive bytes must already be accepted and retained before removal; zero-byte or other obstruction blocks. |
| Complete | Verify filesystem, then commit item, terminal operation and cursor together with exact row/state checks | A failed conditional update or injected transaction failure rolls back all three changes. A committed completion resumes without rewriting files or uploading again. |
| Classify and prune | Observe exact prepared/terminal publication; use existing bounded classified-history eligibility | A rejected rename can age out normally. Pending rejected saved content, unobserved echoes and the applied tip remain protected in the exercised fixture. |

The completion commit point is the successful SQLite transaction after filesystem verification. Acceptance and local completion are separate observations. Rejection itself makes no local rename effects; later synchronization may legitimately apply a competing accepted rename or deletion.

The existing cooperative working-root lease is represented by a real exclusive Windows file handle. A concurrent modeled Watch call observes handle contention. After release, capture enumerates actual files and suppresses a partial destination only when its prepared operation, predecessor, catalog, materialized base and accepted fingerprint match. The positive untracked-file control still creates and uploads another file while the rename is pending. There is no blanket pending-rename capture bypass.

## Executed cases

| Group | Count | Result established |
| --- | --- | --- |
| Two-copy success | 1 | One item/new name, unchanged rename-only content revision, exact bytes and no extra old names. |
| Durable/filesystem interruption boundaries | 20 | Before/inside/after intent; freeze, decision/receipt, preparation, staging/publication, source removal and completion. Progress remains at the predecessor until completion commits; recovery publishes the destination once. |
| Rejection interruption boundaries | 3 | Before/inside/after retirement; no rejection filename or cursor effects; unrelated changes still synchronize. |
| Identity and receipt replay | 3 | Duplicate pending invocation resumes; a different name cannot replace it; receipt replay survives later namespace changes; changed request under the same ID is rejected. |
| Competing namespace and remote deletion | 2 | Another item wins the name, or the source is deleted; existing rejection outcomes and preserved local source until ordinary pull. |
| Compatible content and saved edits | 5 | Content before/after rename; edits at source/destination after publication; stale saved content preserved in a modeled conflict sibling. |
| Admission and apply obstructions | 6 | Occupied destination, zero-byte source/target, catalog changes after acceptance/before publication and late destination obstruction stop affected effects. |
| Rejected saved content | 1 | Deleted-item content rejection retains bytes and remains pending; rename retirement is not generalized. |
| Watch controls | 2 | Real lease contention, positive changed-file upload, observed partial destination suppression and positive untracked-file upload. |
| Cancellation | 1 | Before submission, the frozen intent remains resumable without remote acceptance. |
| Failed exact-update guards | 3 | Operation/repository-record changes before completion and operation change before rejection abort the transaction; subsequent recovery succeeds. |
| Existing pruning eligibility | 2 | Lost rejected receipt replays and ages out under a retained-count filter; applied tip, unobserved echo and pending rejected content remain. |
| Total | 49 | All passed. |

Every converged copy checks the exact set of live filenames, retained bytes and final applied progress. Restart checks no new upload and unchanged file timestamps. Every rename interruption checks the cursor directly and requires one physical destination publication across failure and recovery, including preservation of a destination timestamp captured before recovery.

## Source mapping and bounded audit

Source references are pinned to the recorded base:

- [Outgoing capture/submission](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.CLI/Library/LibrarySynchronization.CLI.fs#L321) already captures saved bytes and classifies prepared destinations. It does not yet originate rename intent.
- [Incoming application](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.CLI/Library/LibrarySynchronization.CLI.fs#L643) freezes preparation and revalidates source/destination; retained-byte publication precedes old-source removal.
- [Local completion](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.CLI/Library/LibraryLocalState.CLI.fs#L585) owns item/operation/cursor atomicity. [Classified pruning](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.CLI/Library/LibraryLocalState.CLI.fs#L724) already admits terminal rows with no accepted change, subject to its other existing protections.
- [Server receipt replay](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.Actors/RepositoryLibrary.Actor.fs#L1319) and [rename acceptance](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.Actors/RepositoryLibrary.Actor.fs#L1595) provide the existing server contract. The experiment adds no server owner or new acceptance rule.
- The [existing hosted incoming-rename tests](https://github.com/ScottArbeit/Grace/blob/8ce7cd4dda7089e801662dfc410bab4dcbf0ff81/src/Grace.Server.Tests/LibrarySynchronization.Windows.Server.Tests.fs#L882) remain production regression evidence at their own revision. They do not establish a new outgoing command.

One read-only scout compared these seams and audited the experiment. It identified two initial shortcuts: a blanket Watch skip and unchecked affected-row counts. Both were corrected before the recorded run. Its bounded follow-up inspection confirmed that actual-path capture controls and in-transaction guards address those gaps; it did not rerun the script or perform production R1.

## Limits and implementation handoff

The experiment uses real filesystem operations, flushes, exclusive leases and fresh SQLite connections. Interruption discards client call state and reopens the stored rows/files; it does not kill a process or cut power. The model uses compact JSON rows, decoded integer cursors, string fixture identities and SHA-256/length. It does not exercise production F# serialization, Microsoft.Data.Sqlite, opaque signed tokens, BLAKE3, the SDK/HTTP/authentication path or manifest upload/download. The request model's predecessor is captured local context, not a proposed new wire precondition.

Server JSON decisions and Watch capture/classification are explicit models. Modeled conflicts preserve both byte values but do not test production conflict naming/allocation. The pruning fixture contains no active baseline or originating-create dependency and uses small retained-count arguments to exercise the existing filter. It does not establish same-path replacement echo behavior. Name normalization, nested-parent derivation and excluded input parsing remain production acceptance obligations through existing validators and path rules. Point-in-time checks do not promise protection against every uncooperative filesystem writer between a check and its effect.

No capability was added to make the algorithm pass. The selected change fits the accepted three-table responsibilities and existing incoming rename path. It needs a narrow durable outgoing intent/receipt representation and rejected-rename retirement, with unchanged saved-content rejection, zero-byte, conflict and retention behavior. Do not copy the simplified harness storage, server, capture or pruning implementations into production.

The result updates the [maintained design and requirements LIB-012 through LIB-014](../Libraries.Design.md#explicit-file-rename-accepted-next-slice), [type/contract propagation](Libraries.Type-Plan.md#explicit-file-rename-propagation), and planned-capability wording in the [Library guide](../Libraries.md#deferred-capabilities). One fresh implementation session may establish the next native child issue from newly refreshed `origin/main`, with one controller and one implementation owner. Co-deliver these documentation changes with that slice; this readiness branch is not required ancestry and is not an enabling production PR.

The hosted acceptance must start from the actual CLI command in A, synchronize the same item into B under the new name, preserve exact bytes and content revision, restart both copies without duplicates/rewrites, and run the demonstrated rejection, obstruction, compatible-edit and capture cases through production seams. Require focused validation, independent R1, one accepted repair pass and bounded R2 if needed, Shape Review and current-revision GitHub Validate. This readiness work runs no product builds or production tests and claims none of those implementation gates.

Stop for another owner/table/database, a durable lifecycle beyond the bounded intent, changed retention/conflict/zero-byte rules, general reconciliation, other gestures/platforms, material delivery-mode change or another enabling production dependency. Later checkpoint candidates remain deferred.
