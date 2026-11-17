# Library additive catalog-adoption experiment

Question: can an onboarded Windows copy adopt one added Library root, retain its cursor and GUID epoch, then resume its real feed without another durable field, table or recovery lifecycle?

**Verdict: proven for the bounded prototype question.** The final matching Release build passed with zero warnings/errors. One complete filtered NUnit run passed **4/4 tests, zero failures and zero skips**, including **24 named local assertion groups** inside the main test. This is not a delivered adoption command. The [accepted design](../Libraries.Design.md#additive-catalog-adoption-accepted-not-implemented) and [production acceptance mapping](Libraries.Catalog-Adoption-Validation.md) distinguish the planned behavior from the experiment.

Source is `9b18c97ab342d319d3bdeaedc5f1d64571913cf5`. The [decision record](https://github.com/ScottArbeit/Grace/issues/1037#issuecomment-5610931795) contains accepted D1-D3; the [complete execution report](https://github.com/ScottArbeit/Grace/issues/1037#issuecomment-5611183841) records findings, retained failures and preservation checks. The run completed on 2026-09-10 UTC, 2026-09-09 local Pacific time.

## Captured artifacts and reproduction

The captured [fixture fragment](Libraries.Catalog-Adoption-Experiment/AdoptionExperiment.fs.fragment), [prototype patch script](Libraries.Catalog-Adoption-Experiment/Apply-Prototype.ps1) and [runner](Libraries.Catalog-Adoption-Experiment/Run.ps1) are copied byte-for-byte from the successful experiment preparation. [Machine-readable results](Libraries.Catalog-Adoption-Experiment.json) record their SHA-256 hashes, every local assertion group and the final TRX hash.

PowerShell:

```powershell
./docs/design/Libraries.Catalog-Adoption-Experiment/Run.ps1 -RepositoryRoot C:/Source/Grace -OutputRoot C:/Source/Grace-artifacts/library-adoption-reproduction
```

bash / zsh:

```bash
pwsh ./docs/design/Libraries.Catalog-Adoption-Experiment/Run.ps1 -RepositoryRoot C:/Source/Grace -OutputRoot C:/Source/Grace-artifacts/library-adoption-reproduction
```

Both commands require Windows, PowerShell 7.6, Docker, repository restore access and the pinned commit in the supplied repository. Choose a new output directory: the runner refuses an existing one. It archives the pinned source, appends the fixture, applies two exact prototype guards only in that archive, builds and runs the four selected tests. It creates no Git branch/worktree and does not edit the supplied checkout. The fixture starts the normal disposable Aspire services and uses test-owned repository data. The files are not included in a production project or solution.

The matching build/test commands were executed successfully during the experiment. The reproduction preparation separately produced all three adapted source files byte-for-byte, and the PowerShell files passed parser checks. The wrapper was not separately executed end-to-end. This documentation change did not rerun the experiment.

Local evidence remains at `C:/Source/Grace-artifacts/library-catalog-adoption-1037/run-20260909/`:

- `build-final-2.log`, `test-final-2.log` and `results-final-2/adoption-final-2.trx` identify the complete passing run.
- `results-final-2/hosted-tracer.json`, `application-interruption.json` in that directory, and three `publication-*.json` records describe actual hosted outcomes.
- `results-final-2/local-cases/` retains named before/residue/restart snapshots and `cases.json`.
- `retained-final-copies/A` and `B` preserve the completed working copies; original test-owned temporary copies remain too.
- `manifest.json`, `reproduction-check.json`, `sqlite-inspection.json` and `environment-and-preservation.json` retain hashes and environment checks. Full source archives, earlier failures and their residues remain separate.

## What actually ran

| Boundary | Executed result and limits |
| --- | --- |
| Windows / SQLite | Real Windows build 26340 and NTFS, .NET SDK 10.0.401, Grace native SQLite/serializer, root lease, operation and materialized-item reads. Final inspection found the existing three Library tables, WAL and `quick_check=ok`. The JSON separately identifies the inspection library; it is not a claim about Grace's native SQLite version. |
| New adoption operation | Modeled orchestration using real catalog/feed reads, local state and a guarded catalog-only SQL transaction. Known verified fixture snapshots supply the clean-tree expectation. This is not a production scanner or command. |
| Real feed and bytes | Separate actual CLI processes onboard A/B, save under the original catalog, pause, resume and synchronize. A adopts before added-root content exists; B adopts afterward with old-root backlog. Both converge using actual authenticated HTTP and retained immutable bytes. |
| Catalog commit interruption | Exceptions before SQL, inside the transaction and after commit. Fresh database reads and repeated invocation preserve all non-catalog state. These are reopened-state checks, not arbitrary process termination or power-loss tests. |
| Incoming application interruption | A real SQLite abort trigger stops completion after B publishes old-root backlog bytes. The original cursor is retained. A new CLI process resumes successfully, without another upload or completed-file timestamp rewrite. |
| Exclusion controls | Active/incomplete participation, partial page, pending incoming and frozen/rejected saved work, dirty/untracked files, occupied and zero-byte added-root files. Seeded states and snapshot comparisons remain explicit fixture evidence. |
| Catalog and feed stops | Removal, replacement, skipped successor, a changed catalog observation, changed epoch and rebaseline use injected values after real reads. Invalid signed-cursor input exercises the actual HTTP rejection. Stale local state exercises the final comparison. |
| Physical and Watch boundaries | A real NTFS junction is rejected without traversing/changing its target. A genuinely contended root-lease waiter cancels. The actual Watch classifier queued behind adoption's lease rereads pause and makes no changes. No full running Watch adoption lifetime or adoption command cancellation was exercised. |
| Save/Reference/DirectoryVersion | Save Reference before addition succeeds and addition rejects `outgoingSystemNotEmpty`. Addition first succeeds; Save Reference and subsequent DirectoryVersion publication under that path reject Library ownership. One overlapping call pair also accepted addition and rejected Save. It did not force internal interleavings and establishes no atomic cross-domain exclusion. |

The 24 stable local names and their individual results are in the JSON. They comprise three commit/restart/duplicate groups, nineteen refusal/retention groups, one canceled-lease group and one queued-Watch group. They are not 24 separate NUnit tests. The three other NUnit cases exercise publication order.

## Finding that changed the prototype

A catalog-row update alone failed. The unchanged CLI published an old-root backlog file and then rejected completion because the accepted change's original catalog differed from the adopted catalog. The initial failed tracer and its state are preserved.

The existing operation already holds the selected application catalog while the immutable incoming change holds its original catalog. The successful prototype retains both. It changes the copied incoming-history check before effects and the copied completion guard to admit only the tested direct predecessor case, while retaining exact operation, predecessor-cursor and repository-row comparisons. Local operation completion remains restricted. It changes no record shape, rewrites no accepted change and adds no schema or lifecycle.

Those two guards are a recommended translation approach, not production code certified for arbitrary catalog transitions. The real command must establish the complete accepted additive preconditions and derive expected disk state from durable materialization. [Production requirements](Libraries.Catalog-Adoption-Validation.md) include those cases explicitly.

Earlier prototype batches exposed two fixture problems: the wrong DirectoryVersion endpoint returned 404, and symbolic-link creation required an unavailable Windows privilege. The final fixture calls the correct endpoint and uses an ordinary junction without elevation. Their failed runs remain available; the final 4/4 count comes from one later complete run. Routine compilation and reproduction-anchor corrections remain in their logs.

## Applicability

This evidence applies to accepted D1-D3 with an unchanged original root and one added root, complete retained feed and the existing operation model. It can inform implementation planning at the pinned source, with a fresh source comparison before coding. Existing saved-object, pause/resume and root-lease tests remain evidence only for their unchanged algorithms.

It does not cover general reconciliation, removals or relocation, fresh multi-root onboarding, automatic adoption/rebaseline, expired-history compaction, other platforms, arbitrary termination, power loss, a complete Watch lifetime or stronger cross-domain coordination. None of those capabilities is added by the readiness assessment.
