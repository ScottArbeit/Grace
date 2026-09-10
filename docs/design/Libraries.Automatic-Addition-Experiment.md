# Automatic Library catalog experiment

**Captured result for Issue #1081, 2026-09-10: proven for the finite algorithm and executed boundaries below.** This is experiment evidence, not completed production acceptance. The complete local capture is `C:/Source/Grace-artifacts/issue-1081/automatic-experiment/`, including source, runner, logs, snapshots and the 36-entry SHA-256 manifest verified before this record was committed. The source report SHA-256 is `B434544387EBFB511E3799927029BC0358B461D06E5BCD1FCC6848F12E828AD6`.

The current requirements are in [Libraries.Design.md](../Libraries.Design.md#automatic-synchronization-of-added-libraries); the [validation mapping](Libraries.Catalog-Adoption-Validation.md) distinguishes this result from both the rejected manual candidate and subsequent production tests. The disposable diff has absolute Windows review labels; use its recorded reproduction script and source snapshot, not that diff as a portable `git apply` input.

**Verdict: proven for the finite additive-catalog algorithm and named runtime seams below.** Production implementation, live Watch and notification acceptance remain pending. The experiment was completed without editing the production worktree or committing runtime code.

## Question and selected approach

Can an enabled Windows copy receive every additive Library, preserve immutable saved operations and signed feed progress, and recover after directory creation, byte publication or database completion stops?

The experiment uses an archived copy of revision `9272567d184b6f1fb8b20b356c7cb3ba82ba8533`. It changes no production checkout. The first console loads copied existing runtime assemblies; its proposed completion check is compiled separately from a copied local-state source file. The hosted experiments compile the archived source with small disposable runtime changes and use real Aspire services, HTTP, signed one-item feed pages, Windows files, SQLite and external CLI processes.

The selected server extension records `AdditiveCatalogVersions` in the existing `LibraryControlDocument`. Catalog addition appends the previous version in the same control write that commits the new catalog and clears its existing pending decision. Removal clears the list. The 128-root limit bounds this list at 128 entries, including a possible initial empty catalog version. Item submission accepts the current version or an explicitly retained additive predecessor, then performs the existing permission, namespace, content and path checks. Catalog administration still requires the exact current version.

This extends the existing catalog commit protocol. It adds no lifecycle, reissue, migration, actor, table or public route. Original request JSON, operation ID/hash and permanent accepted or rejected receipts remain unchanged. Already rejected requests continue to return the same permanent rejection.

## Executed evidence

| Requirement | Evidence |
| --- | --- |
| Catalog selection and root creation after interruption | `Program.fs`: six `rootsCase` cases, real SQLite and Windows directories; before selection, before/after transaction commit and before/after root creation. Reopens state and repeats the action. |
| Immutable pending input | `pendingCase`: captured, frozen, uploaded, rejected and accepted operations remain byte-identical while unrelated incoming completion uses the existing runtime transaction. |
| Original guard controls | Existing runtime rejects old accepted prepared completion, catalog rewrite, frozen-request rewrite and more than one missed predecessor. Its supported one-predecessor incoming control succeeds. |
| Accepted completion after selection | Six `completionCase` cases use a copied runtime completion transaction with current application-root checks. Failure before/after publication, before/inside/after completion; original operation catalog, request and accepted result stay unchanged. |
| Actual paged HTTP and fresh-process recovery | `AutomaticCatalogExperiment`, `hosted-run-v2.log`: Passed 1, Failed 0, Skipped 0. Two active copies, two additions, one-item signed pages, missing root creation, old-root backlog, lost accepted receipt recovery, real stale-policy rejection retention and byte-publication/SQLite-completion interruption. |
| Permanent rejected receipt | Same hosted test resubmits the exact rejected request and receives the identical rejected result. This showed why a scheduling-only change would leave ordinary saves stranded. |
| Additive predecessor admission and current checks | `KnownAdditiveRequestsKeepImmutableIdentityAndCurrentChecks`: actual actor accepts current, one-old and two-old unchanged commands; unknown version rejects; permission, slot, namespace, content and path checks remain effective; accepted and rejected receipts replay exactly. |
| Catalog commit recovery | `CatalogRestartMaintainsExactBoundedVersions`: five cases around receipt/control write effects, fresh actor instance, duplicate replay, exact list and cleared pending decision. |
| Removal and capacity | `RemovalResetsAllowanceAndRootLimitBoundsHistory`: adds through 128 roots, rejects one more through actual catalog command admission, resets list after a saved removal decision, rejects an old version afterwards. |
| Serialization | Existing `PopulatedFinalLibraryRpcGraphRoundTripsThroughProductionOrleansSerialization` runs with a populated additive-version list. |
| Compatible saved work and actual capture | Final `CompatibleCatalogHostedExperiment("captured")`, `("accepted")`, and `("prepared")` all pass. An unchanged old captured request completes, an accepted request survives an addition during submission, and older prepared local bytes reopen after addition without recapture or reupload. |
| Older prepared incoming after another addition | Each final hosted case publishes old-root bytes, injects SQLite completion failure, adds another empty Library before reopening, then completes the same incoming operation with unchanged catalog/accepted result/predecessor/ancestry and no reupload. |
| Initial all-root enable and mid-baseline addition | Each final hosted case enables a third empty copy against several populated Libraries. Another empty Library is added during its first baseline content read. The original selected baseline completes, then ordinary additive catch-up creates the new root. |
| Obstructions and stale observations | Final console tests protect a file, occupied directory and real Windows junction, and refuse an obstruction inserted between initial and final reads. Exact old repository and operation rows and obstruction bytes remain unchanged. |

Console evidence: `build-extended.log` and `run-extended.log`. Actor and serialization evidence: `actor-build-v5.log`, `actor-run-v5.log`, `actor-results-v5/actor.trx`: Passed 8, Failed 0, Skipped 0; build has zero warnings and errors. Earlier failed builds and test attempts are retained and are not counted as passing evidence.

Final console evidence including obstruction checks: `console-final-build-v2.log` and `console-final-run-v2.log`, exit 0. Creating a symbolic link lacked Windows privilege; the successful reparse case uses an ordinary Windows directory junction instead.

Final hosted evidence: `compatible-final-build.log` has zero warnings and errors; `compatible-final-run-v2.log` and `compatible-final-results-v2/compatible.trx` report **Passed 3, Failed 0, Skipped 0**, duration 51 seconds. The three durable snapshots are `compatible-final-results-v2/hosted-compatible-captured.json`, `hosted-compatible-accepted.json`, and `hosted-compatible-prepared.json` in that same directory.

Two earlier captured-case attempts timed out at A's unrelated `/storage/finalizeManifestUpload` before reaching B's saved-work test; the backend cause is unproven. The final fixture permits one fresh-process retry only for the explicit HTTP timeout and verifies the same operation/request/catalog completes. **The passing final 3/3 run did not take that retry branch.** Separately, a final attempt failed shared setup when a new Service Bus AMQP receiver timed out after a readiness peek had passed; that cause is also unproven. No test body ran in that attempt. The unchanged binary was rerun in a fresh host and passed, without infrastructure changes. These distinct failures remain in `compatible-run.log`, `compatible-run-v2.log`, and `compatible-final-run.log`.

The focused actor storage retains records across new actor instances and injects storage responses. It seeds the existing saved catalog decision to exercise the real completion protocol; it does not claim Cosmos query execution for those cases. The hosted test supplies the real Cosmos/HTTP/provider evidence separately.

## Effect order and recovery

1. Acquire the existing working-root lease, read active participation and authenticated catalog, validate supported additions and missing/ordinary-empty new roots, then reread catalog and local state before selection.
2. Commit only selected catalog metadata. Keep identity, epoch, cursor, page token, items and typed operations unchanged. Selection does not mean content was applied.
3. Create missing ordinary root directories idempotently. Reject file and reparse obstructions. Reopen the same selected row after interruption.
4. Retain frozen requests; submit unchanged requests under the bounded additive-version rule. Recover existing receipts first. Do not replace permanent rejections.
5. Read genuine ordered retained pages from the applied cursor and retained page token. Validate affected roots and current local state before effects; never invent ordering for catalog GUIDs or skip unsupported history.
6. Publish verified bytes, then complete the existing item/operation/cursor transaction. After interruption, compare exact prepared input and physical residue; finish without rewriting the request or accepted result.

The actor's receipt-before-control catalog commit remains unchanged. An interrupted add derives the same list from its existing saved expected/chosen version decision. A successfully stored response that is lost is confirmed by the existing exact reread.

## Required production propagation and remaining checks

Production implementation must explicitly own the `LibraryControlDocument` field and Orleans ID, initial control constructors, catalog completion, item submission, serialization fixtures, local catalog selection, application guards, Watch classification and scheduling, initial multi-root baseline handling, focused tests and maintained documentation. No new HTTP DTO field, route, SDK method, OpenAPI shape or generated client is required solely for the private control-record field. The meaning of the existing item-submit catalog precondition must be documented.

The prototype is not a finished runtime design. Candidate work must retain added-root obstruction checks before committing selection, refresh Watch's classification, recover catalog changes during a running tick without terminating Watch, emit the existing best-effort wake and retain periodic/startup reads. Live Watch, notification delivery, cancellation and command-level negative obstruction cases remain candidate tests. Mid-baseline addition and initial multi-root enable were executed in the final hosted cases.

The scheduler must combine the two tested behaviors: valid old captured/frozen requests continue under bounded server compatibility, while already rejected requests remain unchanged and do not prevent unrelated incoming work. The earlier hosted variant tests rejection retention with incoming progress; the final compatible variant tests valid queued work completing. Do not claim that the final compatible binary reran the earlier rejection-specific fixture. Retained rejection should remain visible as blocked work, without receipt retirement or automatic reissue.

Baseline completion keeps its original selected metadata and application boundary. A compatible addition during installation does not replace that baseline or reset applied files; the copy finishes it and then selects/adds the new roots before retained-feed catch-up. Watch must refresh after that transition.

Required guard inventory: catalog selection prevalidation/reread/CAS; `submitLocal` old-operation handling; `applyChangeWithCancellation` current-row and root checks; its `revalidate`; `captureSaved` prepared-publication classification; `completeWith`; baseline empty-root admission and current-state checks; existing page/cursor/operation immutability checks. Removing only one catalog comparison is insufficient.

The actor failure tests start from saved catalog decisions and exercise the real existing repair protocol. They do not test Cosmos enumeration during catalog admission; the hosted administrator calls supply that separate evidence. The capacity case starts with one configured root, reaches 128 roots and 127 recorded predecessors, then rejects another root. The initialized empty catalog can contribute one additional predecessor within the same 128-root bound.

Source preservation and review artifacts: `prototype-source-files.json`, `prototype-source/`, `disposable-source.patch`, `artifact-hashes.json`. `Run-Hosted.ps1` recreates a new archive and copies the exact final source snapshot before building/running the selected actor and hosted tests. Its PowerShell syntax was parsed; its component build/test commands were executed as recorded, without adding a redundant whole-script reproduction run.

## Commands

PowerShell, from the disposable `source` directory:

```powershell
dotnet build src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release -v quiet
dotnet test src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release --no-build --filter 'TestCategory=AutomaticCatalogActorExperiment|FullyQualifiedName~LibrarySerializationTests'
dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release -v quiet
dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'TestCategory=CompatibleCatalogHostedExperiment'
```

The earlier `AutomaticCatalogExperiment` captures the scheduling-only stage before the actor compatibility extension; its assertions intentionally expect a stale-policy rejection. Its preserved passing log is historical within this experiment and should not be rerun unchanged against the extended actor.
