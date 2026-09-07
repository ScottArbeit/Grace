# Operations usage observation boundary experiment

[Issue #1054](https://github.com/ScottArbeit/Grace/issues/1054), under [Issue #554](https://github.com/ScottArbeit/Grace/issues/554). Discovery result, September 7, 2026. The proposed durable observation tracer remains **Exploratory**.

The current SQL store safely accepted the supplied positive facts in the finite failure/retry controls below. It is **not directly admissible for either delivered point diagnostic**. Different fact IDs add repeated values, known zero is rejected, timestamps lose seconds, and the contract cannot retain source-specific count or enumeration window. ID deduplication does not establish immutable capture, complete repository coverage, or elapsed usage.

This result helps Grace preserve useful observations without turning an operator's repeated diagnostic into increased minute usage. No production code, type, schema, package, runtime, diagnostic, or accounting behavior changes here.

## Question, revision, and environment

Question: which current supplied-fact ingestion guarantees can the next durable source-specific observation reuse, and which capture/measurement semantics still need a decision?

- Source base: `f849d72275eab54c95736e90f50369f06f761cd6`, the reviewed PR #1052 candidate. Eventual delivery comparison: main `9f54fe14626cd718af88890b731f8518f9b06e34`. The approved stack is preserved; this issue adds only its three documentation/evidence paths.
- Windows, PowerShell 7.6, .NET SDK `10.0.400`; matching Release builds of current `Grace.Types`, `Grace.Operations`, and `Grace.Operations.Data`. Production and disposable harness builds passed with zero warnings/errors.
- Dedicated SQL Server Developer Linux container `grace-ops-1054-sql`, bound only to `127.0.0.1:14339`. Image ID/digest: `sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89`.
- Isolated databases `GraceOps1054_primary` and `GraceOps1054_replay`. Current `OperationsUsageSchema.CreateDatabaseIfMissing` was used only for fixture initialization. Each run asserts initially empty usage tables and never clears them.
- Ephemeral fixture-only SQL credentials stayed outside Git in the retained external fixture directory. The [evidence bundle](Operations.UsageObservation-Boundary-Experiment.json) contains an environment-variable contract, executable source, captured inputs, expected/observed rows, output, and hashes. No real account credentials are included.

The current compiled [usage contract](../../src/Grace.Types/Usage.Types.fs), [shared serializer](../../src/Grace.Shared/Constants.Shared.fs), and [SQL store](../../src/Grace.Operations.Data/OperationsData.fs) ran. This is real SQL Server behavior, not an Azure SQL deployment, Service Bus delivery, HTTP request, Orleans restart, source enumeration, or billing test.

## Effect sequence and failure model

The compiled store validates and creates a `UsageFactPersistencePlan`, opens the real SQL transaction, inserts the raw fact if its ID is absent, adds its quantity to the aggregate only when the raw insert succeeds, and commits. The raw insert uses `UPDLOCK, HOLDLOCK`; the aggregate merge uses `HOLDLOCK`. These are current implementations, unchanged by this experiment.

Disposable object expressions wrap the existing transaction interfaces. They delegate writes to `SqlOperationsUsageTransactionScope` and inject exceptions before the first write, immediately after the real raw insert, after the operation has completed both writes but before returning to the real commit, or after the real scope has returned from successful commit. The existing scope rolls back the first three failures. The last injection discards the successful acknowledgment; it does not simulate a network outage.

After each injected failure, a separate connection queries committed raw and aggregate rows. The harness discards caller state, clears connection pools, reloads the same captured input bytes, and constructs fresh store/scope/connection objects for retry. Pooling is disabled. This models object reconstruction over durable SQL state; no process was killed and no host or power-loss durability was exercised.

## Finite SQL results

Both runs passed **21 checked ledger entries**, covering the eight charter families. The second run extracted the executable and inputs from the checked-in JSON into a new external directory, verified every embedded file and pinned source hash, built matching assemblies, and used a second fresh database. Its complete stable ledger matched the first run byte for byte.

| Control | Returned result | Independently queried durable result |
| --- | --- | --- |
| Positive supplied value `17` | `Accepted` | One raw row, aggregate `17`. |
| Identical ID/payload, fresh objects | `AlreadyProcessed` | Same raw row and aggregate `17`. |
| Distinct ID, same scope/minute/value `17` | `Accepted` | Two raw rows, aggregate **34**. |
| Original ID, quantity changed to `99` | `AlreadyProcessed` | Original raw quantity stays `17`; aggregate stays `34`. |
| Original ID, changed owner | `AlreadyProcessed` | Original owner and every other raw field retained; no changed-owner aggregate. |
| Original ID, changed organization | `AlreadyProcessed` | Original organization retained; no changed-organization aggregate. |
| Original ID, changed repository | `AlreadyProcessed` | Original repository retained; no changed-repository aggregate. |
| Original ID, changed storage pool | `AlreadyProcessed` | Original pool retained; no changed-pool raw or aggregate row. |
| Quantity `0` | `Invalid: Quantity must be greater than zero.` | No raw or aggregate rows in the invalid scope. |
| Kind `99` | `Invalid: FactKind '99' is not supported.` | No raw or aggregate rows in the invalid scope. |
| Direct record with seconds `12:34:56Z` | `Invalid: ObservedAt must be normalized to a UTC minute boundary.` | Plan rejected; no raw or aggregate rows. |
| Before first write; fresh retry | Injected failure; then `Accepted` | Empty residue; retry produces one raw row and aggregate `23`. |
| After raw insert; fresh retry | Injected failure; then `Accepted` | Both writes absent after rollback; retry produces one raw row and aggregate `23`. |
| After aggregate, before commit; fresh retry | Injected failure; then `Accepted` | Both writes absent after rollback; retry produces one raw row and aggregate `23`. |
| After successful commit, acknowledgment discarded; fresh retry | Injected failure; then `AlreadyProcessed` | One raw row and aggregate `23` before and after retry. |
| Two identical concurrent calls | One `Accepted`, one `AlreadyProcessed` | Exactly one raw row and aggregate `31`. |
| Pre-cancelled valid attempt | `Cancelled` | No raw or aggregate rows in its scope. |

Each of the four failure rows represents two ledger entries: residue and restart. Concurrent calls signal a two-participant readiness count, wait on one explicit start gate, and have a 30-second completion bound. There are no arbitrary sleeps. This single pair per run does not establish load capacity or all possible SQL interleavings. Pre-cancellation is the only cancellation timing exercised.

Queries read all nine immutable raw fields and all seven aggregate key/value fields through independent connections. Expected rows are constructed from the explicit fixture facts and totals, independently from returned persistence results. Both original and changed-pool scopes are queried for conflict controls. Database-maintained `CreatedAtUtc` and `UpdatedAtUtc` are excluded from stable comparison; they do not represent observation time.

The same-ID conflict behavior is applicability evidence, not a declared defect in the current supplied-fact tracer. Its lookup asks whether the ID exists; it does not compare the incoming immutable payload. `AlreadyProcessed` therefore cannot serve as confirmation that a changed observation was captured. The durable original is preserved.

## Time and serialized-contract gaps

The executed constructor receives `2026-09-07T12:34:56Z` and emits `2026-09-07T12:34:00Z`. The real plan and raw row retain that minute boundary. Supplying seconds directly to a record fails validation rather than preserving greater precision.

The bundle contains exact current Grace serialization of every supplied input and the successful plan. `UsageFact` has `Class`, `UsageFactId`, `CorrelationId`, `FactKind`, `Scope`, `Resource`, `Quantity`, and `ObservedAt`. It has no source-specific count, enumeration start/end, coverage declaration, or snapshot evidence. The captured zero input explicitly serializes numeric `Quantity: 0`; deserialization preserves zero, and current validation rejects it because quantity must be greater than zero. The harness does not weaken that contract.

The delivered [DirectoryVersion and TextContent diagnostics](../Operations.md) each retain their own byte/count names and enumeration window. Their zero values are meaningful finished observations. Those contracts were inspected here, not executed as SQL producers. Source completeness remains a separate question: neither diagnostic covers all repository content, and adding their numbers cannot establish complete storage usage. Repeating an observation or running it every minute cannot reconstruct the missing historical interval.

## Historical salvage, inspected only

The read-only comparison used `OperationsUsageJournal.fs` at `aa8999493cf1a6c1f7e13b2b8986460b5e0acd89`, under its historical `src/Grace.Operations/Grace.Operations.Data` path. No historical assembly or SQL executed, and no old branch ancestry or implementation was imported.

| Historical idea | Evidence in that file | Applicability |
| --- | --- | --- |
| Capture immutable payload before send | `AppendAsync`, `insertPendingAsync`, `RawPayload` | Useful design input if a retryable dispatch path is selected; current store acceptance alone does not create pre-send capture. |
| Compare the complete same-ID payload | `matches`, append/process conflict branches | Useful for distinguishing an identical replay from a different observation under a reused ID. |
| Recheck current durable state before dispatch | `TryGetPendingAsync` | Useful if pending dispatch exists; a scan result alone is insufficient. |
| Accept payload and state atomically | `ProcessAsync`, `markAcceptedAsync`, `executeAsync` | Useful ordering idea. The current raw/aggregate transaction was independently exercised above. |
| Billing-completeness locks | `acquireScopeAsync`, `scopeFor`, `BillingCompletenessScope` | Do not port: billing is outside the next observation question. |
| Rejection and repair lifecycle | `RejectAsync`, `RepairAsync`, rejection evidence | Do not port: these are additional durable product semantics. |

The historical file calls `SqlAcceptedFactMutation`, which is a dependency rather than behavior established by this experiment. EF relocation, old migrations, archive, pricing, and billing-close integration remain outside salvage scope. Similar names or historical tests do not establish applicability to the current diagnostic contracts.

## Next tracer recommendation and open decisions

**Recommended, not accepted:** let a SystemAdmin explicitly request a durable DirectoryVersion declaration observation under a caller request ID. The first committed complete observation wins; retry/read returns that stored result. Keep its source-specific bytes, distinct-content count, scope, and read window, including zero. Start with one source; keep the TextContent diagnostic independently useful and apply the resulting design later only if its distinct meaning fits. Reuse Operations SQL through its data boundary, with the existing worker and additive fact accounting unchanged and disconnected from these non-billable observations.

A source-specific durable observation needs a production contract beyond the current positive minute fact. Prefer one immutable completed-observation row over changing `RepositoryStorageBytesMinute` to admit incompatible meanings. Compare this with a pre-send journal: a manually requested SQL capture can commit its finished result without a broker send, pending/rejected states, or a dispatcher. A failed attempt before commit leaves no row and may enumerate again on retry; it does not promise preservation of an uncommitted reading. Keep historical observation values separate from arithmetic minute contributions until their coverage and time interpretation are accepted. This is a recommendation for the next design decision, not a new production declaration or an accepted capture lifecycle.

| Decision | Recommended default | Status and consequence |
| --- | --- | --- |
| First durable source | DirectoryVersion declaration diagnostic, explicit SystemAdmin request | Recommended. One useful durable result without introducing another scanner or repository-total promise. |
| Request identity and stored-result lookup | Caller supplies one request ID bound to source and verified repository scope; check for a stored result before reading again | Recommended. Owner must accept ID ownership, source/scope collision rejection, and authorization on retry/read before coding. A new ID requests a new observation. |
| Competing complete reads | The first committed complete result under that scoped request wins; other callers return the winning row, including its original window | Recommended. Unlike full-payload replay matching, two attempts may finish different reads before a row exists. Owner must accept that winner behavior; stale local results cannot overwrite or masquerade as the committed result. |
| Capture and response ordering | Complete the source read, atomically insert or select the winning row in Operations SQL, then return that stored result | Recommended. No pending/rejected/dispatch state. Decide the internal SystemAdmin capture/read surface and data access owner while preserving the existing read-only diagnostic. Precommit failure leaves no observation; retry may read again. |
| Measurement meaning | Preserve source, scope, nonnegative bytes/count, and full read window; never infer interval coverage | Recommended durable contract. Existing diagnostic meanings remain accepted; no billing meaning is added. |
| Additive minute accounting | Leave the current contract intact | Required preservation for this issue. Any future observation-to-meter conversion needs a separate accepted coverage/time rule. |

The smallest subsequent experiment is a disposable **one-row SQL acceptance experiment** after those contract decisions are accepted. Use explicit completed source results, including zero, with distinct read windows. Exercise failure before insert, after insert/before commit, and after commit with discarded acknowledgment; reconstruct fresh objects and require retry to return the exact committed row. Gate concurrent same-ID attempts carrying different completed reads and require both responses to identify the first committed winner without addition or replacement. Reject source/scope collisions without exposing another scope's result. Establish that failed source enumeration writes no row, and that an existing stored result can be returned without another enumeration. No broker or multi-state journal is needed for that selected model. Reuse this issue's transaction evidence only for unchanged supplied-fact mechanics; it does not establish the proposed one-row acceptance algorithm.

No scheduler, timer, archive, billing locks, repair workflow, public owner API, new content owner, Library/Cache dependency, or combined repository measurement is selected. Stop before production assignment if request identity, capture ownership, or first-committed-result behavior remains unresolved. This Discovery slice is complete; the proposed durable observation is not Design-ready or Plan-ready.

## Replay and retained evidence

The bundle embeds `Program.fs`, `Experiment.fsproj`, `global.json`, `replay.ps1`, and all 16 captured input files. There are **zero named harness types and zero new production types**. `UsageBoundaryExperiment` is one disposable module of functions. Its adapters are object expressions over the two existing transaction interfaces. Existing reused Grace shapes are `UsageFact`, `UsageFactKind`, `UsageFactScope`, `UsageFactResource`, `UsageFactPersistencePlan`, `RawUsageFact`, `UsageAggregateMinuteKey`, `UsageAggregateMinute`, `UsageFactPersistenceResult`, `UsageFactPersistenceStatus`, and the existing schema/store/SQL scope and bootstrap mode. Compiler-generated anonymous/object-expression types are not new domain declarations.

Set `OPS1054_SQL` through a local secret mechanism to the documented dedicated fixture endpoint and a fresh `GraceOps1054_` database. Use the pinned SQL image. Keep its port, database, and credentials isolated. The replay script rejects a different endpoint, existing nonempty usage tables, changed pinned sources, modified embedded inputs, and an existing output directory. It performs no resource deletion or container management.

PowerShell, from the issue checkout after fixture setup:

```powershell
$evidence = (Resolve-Path './docs/design/Operations.UsageObservation-Boundary-Experiment.json').Path
$bundle = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json -Depth 100
$entry = $bundle.executionFiles | Where-Object name -EQ 'replay.ps1'
$scriptPath = Join-Path $env:TEMP ('ops1054-replay-' + [guid]::NewGuid() + '.ps1')
[IO.File]::WriteAllText($scriptPath, $entry.text)
if ((Get-FileHash -LiteralPath $scriptPath).Hash.ToLowerInvariant() -ne $entry.sha256) {
    throw 'Replay script hash mismatch.'
}
& $scriptPath -Evidence $evidence -RepoRoot (Get-Location).Path `
    -OutputDirectory (Join-Path $env:TEMP ('ops1054-run-' + [guid]::NewGuid()))
```

bash / zsh may invoke the same extracted PowerShell entrypoint on the supported Windows host, with its fixture environment already set:

```bash
pwsh -File "$SCRIPT_PATH" -Evidence "$EVIDENCE_PATH" \
    -RepoRoot "$REPO_ROOT" -OutputDirectory "$NEW_EXTERNAL_OUTPUT"
```

Stable ledger SHA-256: `3ebe8c8f698d7325e3c005a6fd5d843f4df91535301a72a3ab4b68243728faf6`. Executed harness source SHA-256: `26e34ccf7c742fe788ca4de4602705c897392107fe91f5cf5cef00e624ea12b2`. The JSON records all executed binary hashes for both runs, ten source/configuration hashes, and every embedded file hash. Rebuilt harness binaries may differ by external path; replay compares the stable rows/statuses and records the actual loaded-directory binaries.

External retained artifacts are under `C:/Users/scott/.codex/visualizations/2026/09/07/01a07acd-5979-7700-889f-51bc642202a5/overnight/1054-experiment`, including build/run logs, inputs, source, SQL log, and extracted replay. The owned container was stopped after both runs; its databases/container and all temporary directories remain. No unowned container was changed.

## Validation and propagation

Matching Release production/harness builds, 21 real SQL controls, 21 extracted replay controls, exact ledger comparison, source/input hashes, PowerShell parse, JSON parse, Markdown lint, and `git diff --check` passed. No routine Fast/Full, `dotnet test`, source diagnostic request, broker end-to-end run, Azure SQL run, host crash, or network outage was run. Those omitted paths are outside this finite question; exact-head GitHub Validate and independent reviews remain the controller's delivery gates.

Only this report, its JSON evidence, and the bounded readiness section in `docs/Operations.md` change. Shared/public contracts, persisted shapes, routes, CLI/SDK, OpenAPI/generated clients, runtime/source/worker code, package/project configuration, and product tests remain unchanged because this issue makes no production behavior change. README, CONTRIBUTING, and AGENTS need no command or workflow update. The accepted ancestor diagnostics and unrelated Library work remain preserved.
