# Operations measurement experiment

**Question:** can current retained Grace sources support a bounded, truthful byte observation without treating partial or unobserved repository content as a complete total?

**Verdict: simplified.** Select a DirectoryVersion declaration diagnostic. The executed model establishes finite capture behavior, and the real Grace serializer/projection establishes the selected metadata meaning. No Cosmos or Azure Blob experiment was executed. No repository-storage total, billing fact, historical interval, or production recovery algorithm is approved by this result.

This is the evidence for [Issue #1045](https://github.com/ScottArbeit/Grace/issues/1045) under [Issue #554](https://github.com/ScottArbeit/Grace/issues/554). The selected next contract is in [Operations](../Operations.md#directoryversion-declaration-diagnostic). The [machine-readable record](Operations.Measurement-Experiment.json) retains the exact harness sources, fixture output, command output, and assertions so the disposable directory can be recreated after worktree cleanup.

## Environment and evidence boundary

- Source: `9f54fe14626cd718af88890b731f8518f9b06e34`, branch `codex/1045-operations-measurement-discovery`.
- Run directory: `C:/Source/Grace-worktrees/issue-1045-operations-measurement-discovery/artifacts/operations-measurement-discovery/`.
- Supported actor: one maintainer, deterministic fixtures, local Windows filesystem, PowerShell 7.6, .NET SDK 10.0.400.
- Real seams executed: Release build of current `Grace.Types`; Grace event serialization/deserialization; `DirectoryVersionDto.UpdateDto`; the unchanged whole-file key helper source; local file writes, rename, and reopen.
- Model seams: provider pages/continuations, physical source removal, and mutation are deterministic file-backed fixtures. Manifest addresses/block labels are fixture labels; no content hash or blob integrity validation is claimed.
- Unexecuted: Cosmos query consistency, real continuation behavior, Azure blobs, Orleans hosting, HTTP authorization, broker/SQL ingestion, load/performance, power-loss durability, and network cancellation.
- The process exposed no Cosmos configuration environment variable names during the bounded check. The run used no cloud resources and did not infer a usable isolated provider from credentials elsewhere on the machine.

The only merged artifacts are documents and evidence. The harness introduces no production type or runtime dependency. `SourceSeam.fsx` uses existing types and plain anonymous projections; the PowerShell capture model uses dictionaries. The Types build outputs are isolated under `artifacts/operations-measurement-discovery/build`.

## Source map at the pinned revision

| Source | Observed behavior and implication |
| --- | --- |
| [DirectoryVersion actor](../../src/Grace.Actors/DirectoryVersion.Actor.fs) and [DTO fold](../../src/Grace.Types/DirectoryVersion.Types.fs) | State persists `List<DirectoryVersionEvent>`. Folding Created recovers direct file declarations. Logical deletion retains them; PhysicalDeleted is a no-op in the fold because the state is about to be removed. |
| [Actor services](../../src/Grace.Actors/Services.Actor.fs) | Existing Cosmos queries select `c.State` using grain type and repository partition, deserialize `DirectoryVersionEventValue`, and fold events. This is a pattern for the next adapter, not an existing complete repository-storage API. Existing filtered/root queries must not be reused as complete enumeration. |
| [Common types](../../src/Grace.Types/Common.Types.fs) | DirectoryVersion owns direct `Files`; FileVersion and FileManifest expose logical `Size`. `RecursiveSize` and relationship counts have different meanings. |
| [Storage keys](../../src/Grace.Shared/StorageKeys.Shared.fs) | Whole-file keys include path and hash-based filename; equal hashes at different paths need not be one object. Manifest identity includes storage pool and address. |
| [TextContent types](../../src/Grace.Types/TextContent.Types.fs), [server](../../src/Grace.Server/TextContent.Server.fs), and [WorkItem events](../../src/Grace.Types/WorkItem.Types.fs) | Retained descriptions include Created and DescriptionSet history. Each TextContent ID has its own object and uncompressed length in its reference. Current DTOs miss superseded and cleared values. Gzip upload can precede event append and cleanup can leave an orphan without known logical length. |
| [Artifact types](../../src/Grace.Types/Artifact.Types.fs) and [actor](../../src/Grace.Actors/Artifact.Actor.fs) | Retained metadata has declared Size and deletion/blob-cleanup fields. Metadata precedes upload; links are not a full artifact inventory. Full retained event documents are needed for metadata discovery. |
| [Library types](../../src/Grace.Types/Library.Types.fs) | Library boundaries remain owned by Libraries and existing content services. This experiment makes no new Library ownership or retained-byte claim. |

Do not call `GetRecursiveDirectoryVersions` as a read-only enumeration: it can create caches/reminders. Do not call `getContainerClient` as a read-only setup operation: it can create a container. Existing known-key manifest diagnostics can cross-check a named manifest but do not enumerate a repository.

## Executed source projection

The fixture constructs six direct references: two references to the same 10-byte whole-file key, one 10-byte whole file with the same hashes at a different path, two references to one 20-byte manifest, and one different 20-byte manifest sharing the same block label. It serializes Created, RecursiveSizeSet(999), and LogicalDeleted events with Grace's configured serializer, deserializes them, and folds them through the existing DTO method.

The result retains six direct files after logical deletion. Key deduplication yields four identities and **60 declared logical bytes**. Recursive size 999 is not added. The experiment extracts the unchanged prefix of `StorageKeys.Shared.fs` through `wholeFileContentObjectKey`, excluding unrelated helpers, and compiles it against the freshly built Types assembly. It does not borrow stale binaries from another checkout.

The projected fixture rows enter the capture model. This tests real projection/serialization semantics without pretending the simplified row file is a Cosmos event document or a production input schema. Production enumeration must additionally validate every event document's resolved scope and required Created data, content-reference consistency, and file/manifest size agreement before using the projection.

## Capture effects and restart behavior

1. Start an empty in-memory accumulator; read a file-backed page and validate each scope, identity, and nonnegative integer length.
2. Deduplicate by exact identity, rejecting conflicting lengths. Follow the explicit continuation only while page and row bounds permit it.
3. Only exhaustion produces a quantity. Failure, missing pages, bounds, and invalid data produce `incomplete` with no quantity.
4. Write the complete diagnostic to a staging file. Publishing is the local same-directory move to the result filename. A reopened reader examines only that published filename.
5. Before write or before publish, interruption leaves no published result; a new attempt starts from the source with a fresh accumulator and result filename. A staging residue is ignored and retained in the disposable directory. After publish, loss of the caller response is resolved by reopening the complete result.

Interruption is simulated by stopping at a named effect boundary and discarding the caller's in-memory state before reading the files again. No operating-system process kill, durable flush, duplicate request ID protocol, concurrent publisher, or cloud transaction is claimed. The model's file publication is experiment evidence only; the selected production query has no persisted capture lifecycle.

| Executed case group | Observed result |
| --- | --- |
| Known bytes, duplicate keys/manifests, shared block labels | 60 bytes, four identities from six references. |
| Repeated unchanged enumeration | Equal captured identity/length maps; observational agreement only. |
| Exhausted empty source | Known zero. |
| Before first page, after first page, before second page, after last page | Interrupted attempt has no quantity; each fresh retry reads 60 bytes. |
| Page bound, row bound, missing continuation target | Incomplete, no quantity. |
| Conflicting length, negative length, nonnumeric shape, foreign scope, sum overflow | Incomplete, no quantity. These are data-quality controls, not claims that valid producers normally emit malformed declarations. |
| Logical retention and later modeled source removal | Real DTO retains 60 bytes after logical deletion; removing fixture rows changes the diagnostic to zero. Blob presence remains unobserved. |
| Mutation between page reads | Read page A=10, then change source to A=30/B=40 before reading B. Captured 50 is neither the before total 30 nor after total 70. Exhaustion cannot establish a snapshot. |
| Next enumeration after mutation | Captures 70; the changed identity/length map detects this observed mutation. Matching maps still would not exclude unobserved changes. |
| Before staging write, before publish, after publish, and fresh retry of unpublished attempts | Reopen sees only a fully published result; fresh retries of unpublished attempts succeed. |

**Result:** 23 capture assertions passed, plus the real source-seam assertions. The capture bounds (four pages and 20 rows by default) deliberately make boundaries cheap to exercise. They are historical model controls, not production capacity measurements. The owner-directed Issue #1047 revision removes fixed production count and elapsed-time limits while retaining exhaustion and caller cancellation.

## Reproduce and inspect

From the repository root, extract the two retained harness files and run the experiment. The JSON record includes only the two permitted filenames; extraction below explicitly checks that list. Run in a checkout of the pinned source revision to reproduce the same source semantics. A later revision is a new experiment and must refresh the evidence.

PowerShell:

```powershell
$record = Get-Content docs/design/Operations.Measurement-Experiment.json -Raw | ConvertFrom-Json
$experiment = New-Item -ItemType Directory -Force artifacts/operations-measurement-discovery
foreach ($file in $record.harnessFiles) {
    if ($file.name -notin @('Run-Experiment.ps1', 'SourceSeam.fsx')) { throw 'Unexpected harness file' }
    $path = Join-Path $experiment.FullName $file.name
    [IO.File]::WriteAllText($path, $file.content)
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Harness hash mismatch' }
}
pwsh -File artifacts/operations-measurement-discovery/Run-Experiment.ps1
if ($LASTEXITCODE -ne 0) { throw 'Experiment failed' }
Get-Content artifacts/operations-measurement-discovery/results.json
```

bash / zsh, invoking the same PowerShell extraction and harness on the selected Windows host:

```bash
pwsh -NoProfile
# Run the PowerShell commands above in that session.
```

The harness retains `build.log`, `source-seam.log`, `events.json`, `rows.json`, `results.json`, and a uniquely named `capture-*` directory containing input pages and publication residues. The captured run also retains `run.log`. The JSON record contains these small outputs; build binaries and transient capture directories stay out of Git. File paths inside captured failure messages identify the original disposable attempt and are not prerequisites for rerunning it.

An additional local archive survives worktree cleanup at `C:/Users/scott/.codex/visualizations/2026/09/07/01a07acd-5979-7700-889f-51bc642202a5/overnight/1045-experiment`. It contains the top-level scripts, extracted source, fixture inputs, logs, result, and a SHA-256 manifest, excluding build binaries and transient capture directories. From that archive, run `Run-Experiment.ps1 -RepositoryRoot <pinned-checkout>` to rebuild and repeat the experiment. The Git JSON record remains the portable copy; the local archive path is not a remote artifact URL.

## Decision and remaining validation

The finite model supports a declaration-specific diagnostic with strict exhaustion and error behavior. It rejects a total-storage claim and removes durable capture, repeated-measurement billing, scheduling, and historical backfill from the next slice. The next slice uses one successful response record and the established Grace error path; unknown is not represented as a nullable successful zero.

Markdown lint, JSON parsing, harness parsing, source build, executable assertions, and diff checks validate this documentation candidate. Production runtime tests, Fast/Full, live Cosmos/Blob tests, and throughput tests were not selected for unchanged production code. Independent review, GitHub Validate, and the HTML Shape Review belong to the controller's candidate delivery checks.

## Issue #1047 real provider preflight

The follow-up [Issue #1047](https://github.com/ScottArbeit/Grace/issues/1047) ran its required provider check before production edits at base `59dd079cc08d75b4c82157fe3198adaf248bb663`. **Result: passed.** An isolated Linux Cosmos emulator used container `grace-ops-1047-preflight`, host endpoint `https://127.0.0.1:18081`, a unique database per attempt, and an `events` container partitioned by `/PartitionKey`. No cloud credentials or shared containers were used.

The [retained preflight record](Operations.DirectoryVersionSize-Preflight.json) contains the executable F# source, PowerShell runner, SHA-256 values, SDK version, image digest, command, and output. The run used Cosmos SDK `3.62.1`, Grace's configured serializer, actual repository and DirectoryVersion events, the existing query wrapper, and the existing DTO fold. It read five retained logically deleted documents across three real pages, obtaining 50 bytes; another grain and repository partition were excluded. An existing repository with no declarations returned zero, an absent repository failed, and deleting the five source documents changed the next enumeration to zero. The preflight reads declarations and does not inspect blob content.

To reproduce, extract the two allowlisted `harnessFiles` from the record into a disposable directory and check their SHA-256 values. Start the recorded image on an unused local port `18081`, wait for its healthy state, then invoke `Run-Preflight.ps1 -RepositoryRoot <checkout>` from PowerShell. The runner rebuilds the checkout's actor project and loads its actual dependencies. The emulator startup command is recorded verbatim in the JSON. Stop and remove only the named disposable container after capture. The original container and its test databases were removed after this run.

The local archive is `C:/Users/scott/.codex/visualizations/2026/09/07/01a07acd-5979-7700-889f-51bc642202a5/overnight/1047-preflight`. It also retains the hosted-test log and the serializer zero-value check. This location is a local artifact, not a remotely accessible URL.

The production implementation adds raw required-field checks before the typed fold so missing source fields cannot silently become empty declarations. The executable zero-value check also established that Grace legitimately omits `FileVersion.Size = 0`, while retaining `ContentReference.ReferenceType` and `Manifest`. The [recorded interpretation](https://github.com/ScottArbeit/Grace/issues/1047#issuecomment-5568841359) accepts that existing zero encoding and rejects explicit null or malformed sizes. Required Created, scope, `Files`, and content-reference checks remain in place; no serializer or persisted shape changed.

Hosted tests exercise the actual SystemAdmin route and configured source reader with 257 retained event documents, duplicate whole-file and manifest identities, zero-length declarations, scope exclusions, conflicting lengths, missing `Files`, explicit invalid sizes, and physical source removal. A real serialized zero-length file returns zero bytes with a positive distinct-content count. The earlier Issue #1045 experiment remains a historical model result; this follow-up adds provider and HTTP evidence without changing its exclusion of snapshots, physical presence, historical intervals, or billing.

## Issue #1047 owner-directed revision

The current diagnostic runs until exhaustion or caller cancellation. Its reader uses the existing actor storage provider selection in `Grace.Actors.Services`; the handler and existing response record belong to `Grace.Server.DirectoryVersion`. Unsupported providers fail before Cosmos access. The server and PowerShell command impose no fixed count or elapsed-time limits. The source boundary, validation, deduplication, and error-without-quantity rules remain unchanged.

Before production edits, a disposable extraction of the starting reader with its count ceilings removed completed 33 pages, 10,001 documents, and 100,001 direct entries. Cancellation after an accumulated first page returned no quantity. These are algorithm examples, not provider capacity or throughput measurements. Regression fixtures use distinct DirectoryVersion IDs and batches no larger than the configured provider page size. The earlier captured JSON experiments remain unchanged historical evidence for their pinned revisions.

An explicit scan can be expensive, can take longer as the source grows, and retains one entry per distinct content identity in memory. This diagnostic is not the selected foundation for routine repository usage measurement. It still makes no snapshot, physical-content, historical-interval, or billing claim.
