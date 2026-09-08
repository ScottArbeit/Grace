# Explicit Library file rename validation

Issue #1073 implements LIB-012 through LIB-014 from main `8ce7cd4dda7089e801662dfc410bab4dcbf0ff81`. The outcome is `grace library rename <path> <new-name>` for one clean synchronized nonempty file in its existing parent, using two onboarded Windows copies and a fixed catalog/root.

## Implementation and limits

The command stores the selected file/name and generated ID in the existing operation row, freezes the namespace-only request and retains the actual receipt. It uses the existing SDK and ordered incoming application, preserving compatible content revisions and saved edits. A definitively rejected unprepared rename becomes terminal and returns before pull. Cancellation checks at submission and filesystem checks retain incomplete work. Completion rechecks repository state inside the SQLite transaction as well as the exact operation row.

No shared DTO, server actor, HTTP route, SDK, generated client, package, solution or deployment change is included. The three Library tables and classified-history pruning policy remain. Production code does not use the disposable experiment's simplified server, capture or pruning models.

The co-delivered [49-case experiment](Libraries.Rename-Experiment.md) retains its original script/result hashes and model limits. Production tests use actual Windows files, the local serializer and Microsoft.Data.Sqlite, real CLI processes, SDK/HTTP, Cosmos/Azurite-backed server acceptance, retained content reads and dual-hash verification. Cancellation uses the same command service with a token canceled after real read preparation. Tests do not claim arbitrary process termination or power-loss safety, nor every experiment interruption as a separately injected production boundary.

## Requirement and interruption mapping

| Requirement or boundary | Production evidence |
| --- | --- |
| Two positional arguments; internal identities excluded | `library rename accepts two positional arguments and rejects internal identities`. |
| Clean admission, zero/dirty/absent/directory/reparse source, occupied local destination, case-only and cross-parent exclusion | `explicit rename admission preserves unsupported sources and destinations`; the reparse case uses a real directory junction without requiring symbolic-link privileges. |
| Intent persisted before effects; cancellation after intent; NFC input normalization; same invocation resumes; different name cannot replace it | `explicit rename intent survives interruption and exact same invocation resumes`. The callback throws after the actual SQLite insert. |
| Request freeze and exact ID across lost accepted/rejected responses; namespace-only precondition; no upload | Hosted `explicit rename command converges or retires its exact namespace intent`, `lost` and `lost-rejection` cases. `pending identity and completion authority remain exact` rejects changed frozen requests. |
| Actual A/B command acceptance, nested parents, stable item/content revision and no repeated upload or completed-file rewrite | Hosted `explicit rename command converges or retires its exact namespace intent`, `normal`, `nested`, `human` and `lost` cases. Both copies compare final items/bytes; fresh CLI restarts compare timestamps and counters. |
| Compatible content before rename; saved content after acceptance at either path | Hosted explicit-command `compatible-edit`, `saved-source` and `saved-target` cases. The first inserts a real remote content acceptance between intent/request and submission. Existing `accepted saved edit and later partial rename save survive process restart` tests both paths and exact stale-content conflict preservation. |
| Cancellation after acceptance and before destination publication | Hosted explicit-command `canceled` case cancels after a real retained-read preparation, asserts unchanged source/destination/cursor and then resumes the same operation. |
| Definitive rejection before any local item/cursor/filename/echo effect; later unrelated catch-up | Hosted explicit-command `competing-rename`, `occupied`, `deleted` and `lost-rejection` cases assert state at command return, then synchronize a separate file. The occupied case is another item winning the destination, distinct from a same-item namespace-version conflict. |
| Rejected receipt retirement interruption, exact-row check and unchanged pruning | `rename rejection retirement retains receipt without item cursor or echo effects` injects a SQLite trigger failure, verifies rollback and retained receipt, retries, rejects stale retirement and checks pruning. Saved-content rejection remains pending in hosted `deletion first retains ItemTombstoned saved edit without resurrection`. |
| Prepared publication after lease release cannot become an upload; changed/untracked positive controls | Hosted `accepted saved edit and later partial rename save survive process restart` obtains genuine acceptance and interrupts production source removal after destination publication. After the CLI exits, it classifies and captures under a new lease, asserts unchanged operation/upload counts for exact destination bytes, then verifies changed and untracked nonempty inputs each upload and converge. Local `prepared rename publication is not uploaded and positive saved observations remain capturable` separately constructs prepared metadata and tests exact capture conditions; it does not establish the server-to-publication sequence. |
| Staging/publication guards, source-removal interruption and completion restart | `Windows source and publication guards preserve changed bytes`; hosted `accepted saved edit and later partial rename save survive process restart` holds the source open after destination publication, then resumes after releasing the handle. Existing `empty visibility continuation and partial page restart preserve every change` exercises committed progress and interrupted page application. |
| Exact repository/operation changes during completion roll back item and cursor | `completion exact record changes roll back item operation and cursor` injects valid changed records after the item write. `completion rolls back item terminal and cursor after actual SQLite failure` exercises a real SQLite failure. |
| Zero-byte source/target, catalog changes, shared lease contention, echo history | Hosted `incoming changes preserve excluded zero files`; local `pending identity and completion authority remain exact`, `Library shares the existing working root lease`, and `coalesced publications bound terminal history while preserving pending origins and the applied tip`. |
| Human and JSON outcomes | `rename human output distinguishes every retained outcome` and hosted explicit-command cases check completed, rejected, ambiguous and accepted-but-incomplete results. The `human` case invokes normal output; other CLI cases invoke `--output Json`. |

## Commands and results

Touched F# files are formatted with Fantomas before matching Release builds. The selected runner is SDK-style VSTest with NUnit; no MTP bridge is configured.

PowerShell:

```powershell
dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-restore
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Library'
dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-restore
dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~LibrarySynchronizationWindowsServerTests'
```

bash / zsh:

```bash
dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-restore
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Library'
dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-restore
dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~LibrarySynchronizationWindowsServerTests'
```

On 2026-09-08, both matching Release builds passed with 0 warnings and 0 errors. The final focused CLI run passed 85 tests, failed 0 and skipped 0. The final hosted Windows run passed 26 tests, failed 0 and skipped 0 in 4 minutes 4 seconds of test execution. Those 26 cases include twelve explicit-command scenarios and fourteen retained synchronization cases, with the two partial-publication cases extended for real capture/upload controls.

Local logs are `C:/Windows/Temp/grace-1073-cli-build.log`, `grace-1073-server-build.log`, `grace-1073-cli-tests.log` and `grace-1073-server-tests.log`; TRX files are in `C:/Windows/Temp/grace-1073-results/`. Markdown lint passed all seven changed Markdown files, relative file links resolved, the experiment scripts parsed, and all four immutable experiment artifacts matched their accepted Git blobs. `git diff --check` passed.

Windows execution is required for the filesystem cases. Required current-revision GitHub Validate, independent R1 and Shape Review remain controller gates. Local Fast/Full was not run as a duplicate broad gate.

## Declaration and scope review

Production additions are the two `PendingOperation` fields, `retireRejectedRename`, `RenameResult`, `selectRenameWith`, `renameResult`, `rename`, `applyChangeWithCancellation`, the two positional argument declarations and `renameMessage`. Existing `submitLocal`, `pull`, `run`, completion and command construction carry the remaining integration. All new declarations have purpose comments. Program's Library help group adds only the `rename` name.

Watch's existing exact prepared/terminal classification remains unchanged. The command adds no automatic rename inference, directory rename, cross-parent move, deletion, reconciliation, retention policy or WDU completion. README, the Library guide, maintained design/type plan and CLI guidance describe the changed command. CONTRIBUTING and Watch instructions require no new workflow or behavior guidance beyond those maintained Library references.
