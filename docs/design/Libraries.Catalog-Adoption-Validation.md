# Library automatic addition validation

**Status: manual candidate superseded by Scott's automatic-sync correction on 2026-09-10.** PR #1082 at `061ab711858f49cf1e9f81949e51b06d753eb9ac` remains unmerged. Its completed R1/R2, Shape Review and [passing Validate run](https://github.com/ScottArbeit/Grace/actions/runs/34430255312) certify the old manual behavior only. The [current design](../Libraries.Design.md#automatic-synchronization-of-added-libraries) and [propagation map](Libraries.Type-Plan.md#automatic-library-addition-propagation) govern the replacement. Captured experiment fixture, scripts and results remain unchanged.

## Automatic behavior still to validate

| Requirement | Required executable evidence | Current result |
| --- | --- | --- |
| LIB-019 | Actual administrator addition of an empty Library; live enabled Watch creates the root and downloads subsequent content without manual commands. Restart/reconnect discovers multiple missed additions. | Experiment covers actual multi-addition CLI/HTTP and initial all-root enable. Candidate live Watch and notification cases remain pending. |
| LIB-020 | Existing saved/pending/rejected work and local edits retained; explicit pause preserved; occupied/reparse target protected; cancellation and stale reads cannot overwrite state. | Experiment covers valid queued work, immutable receipts, older prepared local/incoming work and four obstruction controls. Candidate pause/cancellation and combined rejected-work scheduling remain pending. |
| LIB-021 | Paged retained backlog across catalog additions; interruption before/after root creation, catalog selection, publication and completion; reopen state and converge without skipping history. | Experiment passes with signed one-item pages, several additions, publication/SQLite interruption and fresh-process recovery without reupload. Production implementation and matching tests remain pending. |

The [captured automatic addition experiment](Libraries.Automatic-Addition-Experiment.md) passed all named console cases, 8/8 actor/serialization cases and 3/3 hosted cases. Final builds had zero warnings/errors. The root verified all 36 captured hashes and the actual TRX counters. The final hosted run used no timeout retry; earlier HTTP manifest-finalization and separate Service Bus setup failures remain recorded with unproven causes. No infrastructure change was made.

The algorithm is ready for a replacement charter and implementation. These results do not certify production files or a running Watch. The guide and design retain that distinction until actual candidate acceptance passes.

The remainder records the rejected manual candidate for comparison and salvage. Its LIB-019 through LIB-021 labels refer to the former requirement wording, not the corrected rows above. It is not a user workflow or a claim of delivered automatic synchronization.

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
