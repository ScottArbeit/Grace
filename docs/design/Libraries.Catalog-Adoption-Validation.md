# Library automatic addition validation

**Status: automatic candidate implemented locally under the replacement charter; fresh review and CI remain required.** The old manual PR #1082 revision `061ab711858f49cf1e9f81949e51b06d753eb9ac` remains preserved. Its R1/R2, Shape Review and Validate results certify only that historical behavior. The [current design](../Libraries.Design.md#automatic-synchronization-of-added-libraries) and [propagation map](Libraries.Type-Plan.md#automatic-library-addition-propagation) govern the replacement.

## Automatic candidate requirement mapping

| Requirement | Production behavior | Candidate evidence |
| --- | --- | --- |
| LIB-019 | Enabled copies select every added root during normal synchronization; Watch refreshes inherited classification and its ignore snapshot without restart. Missing ordinary roots are created. Startup and periodic authenticated reads recover missed hints. | Actual `library add` during `live Watch and CLI pause retain local saves while another copy continues` creates an empty root without moving the content cursor, then downloads and uploads added-root content while preserving the VC Save boundary. Hosted `AutomaticCatalogSynchronization` cases cover several missed additions, initial all-root onboarding and an addition during baseline download. |
| LIB-020 | Exact queued requests survive additive catalog changes; current permissions/path/namespace/content checks still apply. Already rejected work stays rejected. Earlier unrelated history may apply, but matching blocked effects cannot be skipped. Explicit pause, occupied/reparse input and cancellation remain protected. | Actor recovery/serialization tests; actual `deletion first retains ItemTombstoned saved edit without resurrection`; three `automatic catalog CLI protects preexisting added root input` cases; actual command cancellation while waiting for the lease; local before-commit obstruction/revalidation/pause/cancellation cases. |
| LIB-021 | Catalog-only selection commits under the lease before idempotent missing-root creation. Original accepted/prepared data stays immutable; genuine signed feed completion alone advances the cursor. | Local `automatic catalog selection reopens without changing unfinished input` and `automatic catalog completion preserves old prepared input`; hosted accepted/prepared/racing cases use one-item pages, publication/SQLite interruption, another catalog addition and fresh-process recovery with unchanged operation input and no residue upload. Actual root-identity validation rejects unsupported history without ordering GUID versions. |

The existing control record adds `AdditiveCatalogVersions: LibraryCatalogVersion array` at Orleans field identity 10. Actual actor cases verify current/known predecessor acceptance, unknown-version rejection, current guards, exact accepted/rejected receipt replay, removal reset, the 128-root bound and failures around receipt/control writes. Catalog administration retains its exact-version precondition. Signed empty/current-cursor wake tests cover cursors 0 and 17 and stable catalog-specific message identities without changing content notification progress. The live Watch proof demonstrates automatic discovery through its notification/periodic mechanism; it does not isolate transport delivery latency from the periodic fallback.

No HTTP route, public DTO, SDK signature, static OpenAPI shape or generated client changes. Existing OpenAPI descriptions do not state the removed exact item-catalog restriction. No project, solution, package, deployment topology, local table or recovery lifecycle changes. The manual CLI command and its inventory are removed; the existing status shape remains. CONTRIBUTING has no affected command statement. Captured manual and automatic experiment files remain unchanged.

## Focused candidate results

All paths below are under `C:/Source/Grace-artifacts/issue-1081/`. Matching Release builds completed with zero warnings/errors before their `--no-build` tests.

| Proof | Result | Log and TRX directory |
| --- | --- | --- |
| CLI Library/local-state/baseline/commands and Watch classification | 123 passed, 0 failed, 0 skipped | `automatic-cli-final-test.log`; `automatic-cli-final-results/automatic-cli.trx` |
| Output inventory and grouped help after corrected counts | 38 passed, 0 failed, 0 skipped | `automatic-contract-fixed-test.log`; `automatic-contract-fixed-results/automatic-contract.trx` |
| Actual actor recovery, existing actor cases, wake and serialization | 25 passed, 0 failed, 0 skipped | `automatic-server-final-test.log`; `automatic-server-final-results/automatic-server.trx` |
| Final hosted automatic matrix | 8 passed, 1 failed; captured case hit HTTP timeout; live Watch passed in 30 seconds | `automatic-hosted-final-test.log`; `automatic-hosted-final-results/automatic-hosted.trx` |
| One captured-only retry after fresh host setup | 0 passed, 1 failed at the same client HTTP boundary | `automatic-captured-retry-test.log`; `automatic-captured-retry-results/automatic-captured.trx` |

The focused filters are `FullyQualifiedName~LibraryLocalStateTests|FullyQualifiedName~LibraryBaselineTests|FullyQualifiedName~LibraryCommandTests|FullyQualifiedName~LibraryCliParsingTests|FullyQualifiedName~CommandOutputContractTests|TestCategory=WatchPathClassification` for the 123-case CLI run, `FullyQualifiedName~CommandOutputContractRegistryTests|FullyQualifiedName~RootHelpGroupingTests` for the 38-case inventory/help run, `FullyQualifiedName~LibraryCatalogRecoveryTests|FullyQualifiedName~LibrarySerializationTests|FullyQualifiedName~LibraryActorTests` for actor proof and `TestCategory=AutomaticCatalogSynchronization` for the nine hosted cases. The broad CLI filter's `CommandOutputContractTests` term matched no registry tests; the separate 38-case run supplies that proof.

The first CLI run stalled and was terminated after identifying its owned testhost; its aborted artifact is retained, and its cause remains unproven. The next bounded diagnostic run passed 91/91, followed by the final expanded 123/123 run. The first output-registry run failed six assertions because removal of the manual command left stale inventory totals; corrected counts and the matching rebuild passed all 38 registry/help cases. The first hosted run passed 6/8: two fixture expectations assumed the old stopping behavior or exactly one materialized file. Corrected assertions identify the original item and distinguish accepted interruption from successful catalog retry.

Hosted v3 passed 8/9. Its captured case timed out at manifest finalization and again during one exact-request retry. The matching server log records a Cosmos DedupeIndex gateway write 408 lasting 65,010 ms while UploadSession waited for `WriteAfterAuthoritativeMetadata`. That explains the observed v3 boundary; the provider stall's cause remains unknown. Dedupe, upload-session and storage code are unchanged from the delivery base. See `automatic-hosted-v3-diagnostic.md` and `automatic-hosted-v3-server.log`. The final nine-case run repeated a captured-case HTTP timeout; this record does not assign that later run the v3 backend cause without its own matching evidence. No infrastructure or timeout changes were made.

One captured-only retry after completed teardown also timed out at `/storage/finalizeManifestUpload` and `/libraries/content/prepare`. Its own backend cause has not been established. No further retry was made. The final candidate therefore retains an explicit local captured-case proof blocker; earlier captured passes and the experiment do not certify this final case. The passing accepted/prepared/racing cases still supply their own unchanged-request and publication-recovery evidence.

Touched F# formatting, maintained Markdown lint and `git diff --check` passed. The implementation owner inspected the complete delivery delta against `9b18c97ab342d319d3bdeaedc5f1d64571913cf5`, including effect order, unchanged request/receipt guards, classification refresh, retained-rejection scheduling, command removal and contract propagation. Captured experiment files are unchanged from the replacement charter's start revision.

Routine local Fast/Full were not run. Required current-head GitHub Validate and fresh review remain controller-owned gates. Windows-only cases guard eligibility before resources are created; portable actor/serialization/SQLite checks remain enabled elsewhere. These proofs do not claim power-loss durability or protection against an uncooperative writer after the final filesystem observation.

## Preserved algorithm evidence

The [captured automatic addition experiment](Libraries.Automatic-Addition-Experiment.md) passed all named console cases, 8/8 actor/serialization cases and 3/3 hosted cases. The controller verified all 36 captured hashes and actual TRX counters before freezing the replacement charter. Its final hosted run used no timeout retry; earlier experiment failures retain their original evidence limits. These results establish the algorithm gate, separately from the production candidate results above.

The remainder records the rejected manual candidate for comparison and salvage. Its LIB-019 through LIB-021 labels refer to the former requirement wording. It is not a supported user workflow or proof of automatic synchronization.

## Historical manual requirement mapping

| Requirement | Production implementation | Exact test evidence |
| --- | --- | --- |
| LIB-019: catalog-only adoption | Actual `library sync adopt-catalog`, existing status/output registry, root lease and exact SQLite row/item/operation comparison; only `catalog_json` is updated. | `catalog adoption retains every noncatalog fact through interruption and duplicate invocation` covers before/inside/after commit and duplicate invocation. `catalog adoption CLI retains paused progress and replays both roots after publication interruption` checks actual human/JSON commands, exact retained rows/items/operations, immutable object hashes and bytes. |
| LIB-020: retained input and refusal | Durable materialized ancestry supplies expected paths/content. Ordinary physical enumeration refuses links before descent. Completed onboarding, durable pause, no unfinished work, unchanged inputs and a complete retained feed response are required. | `catalog adoption refuses changed or unsupported inputs without capturing work` (21 cases); `catalog adoption preserves unfinished work of every operation family` (8 cases); `catalog adoption refuses reparse roots ancestors and descendants` (3 cases); `catalog adoption clean tree follows materialized parent identity and rejects cycles` (2 cases); `catalog adoption transaction compares complete materialization and terminal operation snapshots` (2 cases). |
| LIB-020: canceled command and queued Watch | Actual command action forwards its cancellation token to the root lease. Existing Watch classification rereads pause under that lease. | `adopt catalog command cancellation while waiting leaves participation untouched`; `catalog adoption cancellation and queued Watch classification preserve paused state`. |
| LIB-021: ordered replay and preserved frozen input | Incoming accepted change keeps its original catalog; the existing operation envelope selects the application catalog. Unsupported accepted history is rejected before incoming capture/publication. Local frozen-request completion remains restricted. | `catalog predecessor completion permits incoming only and preserves accepted metadata` (2 cases); unsupported-history case in the 21-case refusal test; actual hosted test below. |
| Command propagation | Grouped help and command registry include adoption with the existing status shape and independent pause. Inventory totals are updated. | `library synchronization accepts exact verbs and repository locators`; `library synchronization schema exposes independent pause setting`; command-registry inventory, schema and recursive help tests. |

The local tests use real SQLite and Windows files. Remote catalog, epoch, rebaseline, unavailable-history and partial-feed responses in local tests are explicit controlled inputs. They do not claim a server retention or epoch transition occurred. The after-commit case deliberately loses the caller result after the production transaction returns, then reopens durable state and retries. Before/inside commit faults exercise real rollback. No test claims power-loss durability or protection from uncooperative writers after the final filesystem observation.

## Actual two-copy tracer

`catalog adoption CLI retains paused progress and replays both roots after publication interruption` runs against the Aspire-hosted server and an authenticated forwarding proxy. Every synchronization/adoption command is a fresh CLI process.

1. Onboard A/B with one root, publish a nonempty file and materialize it in B.
1. Publish another original-root edit in A and leave it pending for B. Pause both using actual commands, then create empty added local directories.
1. Add the second root through the existing administrator HTTP operation. Adopt in A through the actual command; check retained pause/row and immutable object hashes. Explicitly resume and publish added-root content.
1. Adopt in B through the actual JSON command, retaining its old cursor, item bases and terminal operation history. Repeat adoption without changes.
1. Inject a SQLite trigger failure at cursor completion after old-root byte publication. Confirm the failed resume is active and retains its old cursor. Remove the test trigger, restart a new CLI process, and converge both old-root and new-root bytes.
1. Repeat a completed run; require unchanged timestamps, no extra submissions/uploads and no WDU completions.

The implementation does not change Watch's lifetime or scheduling. Users restart Watch to load the catalog and explicitly resume. The queued classifier test verifies the unchanged pause boundary; it is not a full running Watch adoption/restart test. Existing `IsInLibrary` point-in-time behavior and Save/Reference/DirectoryVersion ordering remain unchanged, as established by the captured experiment.

## Validation commands and run record

The initial matching Release build of `Grace.CLI.Tests` passed with zero warnings/errors before semantic edits. Adding `adopt-catalog` to the parser test then produced the expected missing-command failure (1 failed, 6 passed). An initial malformed filter matched a spaced F# name incorrectly; the corrected module filter produced that actual regression result.

PowerShell:

```powershell
dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --nologo -v quiet
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Library|FullyQualifiedName~CommandOutputContractRegistryTests|FullyQualifiedName~RootHelpGroupingTests' --logger 'trx;LogFileName=issue-1081-cli.trx' --nologo -v quiet
dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --nologo -v quiet
dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'TestCategory=CatalogAdoption' --logger 'trx;LogFileName=issue-1081-adoption.trx' --nologo -v quiet
```

bash / zsh:

```bash
dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --nologo -v quiet
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Library|FullyQualifiedName~CommandOutputContractRegistryTests|FullyQualifiedName~RootHelpGroupingTests' --logger 'trx;LogFileName=issue-1081-cli.trx' --nologo -v quiet
dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --nologo -v quiet
dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'TestCategory=CatalogAdoption' --logger 'trx;LogFileName=issue-1081-adoption.trx' --nologo -v quiet
```

These tests require the selected Windows host; the hosted command also requires the existing Docker/Aspire services. An earlier local run passed 74/74. Expanded selection later reported 175 passed and 6 failed because adding a command changed six registry/doc inventory assertions; the final candidate updates those totals. The first hosted attempt rejected the server's insertion-order root array because the new check incorrectly required sorted roots. The corrected implementation validates normalization/overlap while preserving the returned catalog. The second attempt reached successful adoption but failed in a test-only envelope deserialization; the test now parses `ReturnValue`. The next hosted run passed 1/1, including publication interruption and new-process recovery. Final counts and report hashes are recorded below after the final test-only assertion additions.

Routine local Fast/Full were not run. The focused Release builds and tests are the local gates; GitHub Validate is the required broad gate. No production server/SDK/shared/generated contract, schema, package, project or solution changed. README and CLI inventory documentation were propagated; CONTRIBUTING has no affected command or workflow statement.

## Initial candidate results at 805a21ed

- Matching Release builds: CLI tests and hosted tests, zero warnings and zero errors.
- Focused CLI/Library/command registry/help run: **185 passed, 0 failed, 0 skipped** in 19 seconds.
- Final hosted run including immutable object-hash retention: **1 passed, 0 failed, 0 skipped** in 17 seconds.
- Touched F# formatted with repository Fantomas. Touched Markdown lint and `git diff --check` pass. Captured experiment files remain byte-for-byte unchanged.
- Reports are local ignored files under each test project's `TestResults` directory. These counts describe `805a21edb550f3cdfec8ac2f3776fb7b35cd11ea`, not the earlier prototype or a passing Ubuntu run.

CLI report SHA-256: `846F60832B1120A8475F011BEF20DF53889044D2C0A99C272499CD3595AFBCDE`.

Hosted report SHA-256: `0A8D50EA85DE50DB97ED740DAA583E474CFA961EB37D8445289A09CE63D5B1A5`.

## Ubuntu CI failure and bounded test repair

[Validate run 34428025370](https://github.com/ScottArbeit/Grace/actions/runs/34428025370) tested `805a21ed` on Ubuntu and showed nine failures in the new command cancellation, queued Watch, clean-tree, changed-input (`stale-row`, `stale-disk`) and four interruption/retry cases. The tests omitted the existing Windows eligibility guard, so production correctly rejected the unsupported platform before the intended test steps. Some negative cases also appeared to pass by catching this platform rejection instead of exercising their named condition.

The root canceled the run after 28 minutes; the CLI assembly had not produced a terminal summary. Cancellation is not a passing result. Other assemblies reported passing summaries, including 1,264 server unit tests and 320 hosted tests with 37 intentional skips. The Windows hosted adoption case was correctly skipped on Ubuntu.

The [frozen CI-1/CI-2 repair ledger](https://github.com/ScottArbeit/Grace/pull/1082#issuecomment-5611789349) permits changes only to test eligibility, owned asynchronous fixture cleanup and this record. Seven Windows command/orchestration/filesystem test functions, comprising 40 cases, now skip before fixture creation on other platforms. The two predecessor-completion cases, two exact SQLite snapshot cases, parser and command-registry tests remain portable and enabled.

Cancellation fixtures now cancel and await their command before asserting that it was waiting. The Watch fixture releases its held lease and awaits its classifier before asserting that it had queued; that wait has a 30-second cancellation limit. Junction creation also has a 30-second limit and kills/reaps its owned subprocess on timeout. On Ubuntu the observed adoption failures happened before lease acquisition, and the fixture-held leases were scoped for disposal. The log does not establish an orphaned adoption waiter as the cause of the missing CLI summary; this repair does not claim to diagnose unrelated hangs.

Production files and the hosted test are unchanged from `805a21ed`. Its actual Windows hosted 1/1 result and report hash above remain applicable and are reused without another hosted run. The repaired head still requires GitHub Validate; local Windows results do not certify Ubuntu execution.

Repair validation on Windows: matching `Grace.CLI.Tests` Release build passed with zero warnings/errors. The same focused Library/registry/help filter documented above passed **185 tests, 0 failed, 0 skipped** in 18 seconds, using `--logger 'trx;LogFileName=issue-1081-ci-repair-cli.trx'`. Both touched F# files were formatted; this Markdown file and `git diff --check` pass. No local Fast/Full or unchanged hosted test was repeated.

Repair CLI report SHA-256: `4A7DF9B7DE00E6515D017346D2E63024AC2A9F46070C563DBB852D66DD17E8EB`.
