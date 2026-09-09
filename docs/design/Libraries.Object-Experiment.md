# Library saved-object experiment

Status: 18/18 cases passed on Windows before production edits at `1e5e0e37fab5fafbf1ac55d8607c10addb8e9dca`. The controller accepted this result in the [algorithm gate record](https://github.com/ScottArbeit/Grace/issues/1073#issuecomment-5594117512) under the [2026-09-08 redesign charter](https://github.com/ScottArbeit/Grace/issues/1073#issuecomment-5594043263).

The [preserved program](Libraries.Object-Experiment/Program.fs) uses real Windows files, Microsoft.Data.Sqlite, the Grace serializer, existing object naming/hash helpers and production metadata cleanup functions. Capture streams to a private same-volume file, flushes and verifies it, publishes without replacing an existing object, verifies final content, then commits a reference. A restart opens fresh SQLite state. The [recorded result](Libraries.Object-Experiment.json) passed all cases.

Eight injected boundaries cover before staging, genuinely partial staging (65,536 of 196,609 bytes), flushed staging, before/after publication, and before/inside/after SQLite commit. Other cases cover object reuse with no timestamp change, fixed locator after working-file rename, uncertain commit, later source changes, writer exclusion, missing/corrupt references, rejected saved-content retention, zero-byte exclusion and cancellation. A 64 MiB input produces operation JSON under 1 KiB and supports streamed verification and a stream-read control.

The cleanup proof seeds a real object-cache directory and file metadata row, invokes existing metadata removal, and verifies those rows disappear while physical content and pending Library work remain. Production Library pruning removes terminal history while preserving pending operations and the shared object. No new table, reference counter or retention lifecycle is justified by this behavior.

## Reproduce the preserved evidence

The original program and result are byte-for-byte artifacts. Program SHA-256 is `ED229F6044057859E70E974DC3EF0E2F24DBE08B012B251EE0AEB0AF58BF3682`; result SHA-256 is `A975F6C264A469874C36EF119F8C393B61EE4659B887281F57AF1DC17793F4CD`.

The reproduction wrapper was added after the experiment to require the exact clean baseline API and run from a temporary directory. It does not mutate the recorded result or use a compatibility facade in current production. Supply a separate preserved baseline worktree; the runner does not create, switch or remove branches/worktrees.

PowerShell:

```powershell
./docs/design/Libraries.Object-Experiment/Run.ps1 -BaselineRoot C:/Source/Grace-worktrees/object-witness-baseline
```

bash / zsh:

```bash
pwsh ./docs/design/Libraries.Object-Experiment/Run.ps1 -BaselineRoot C:/Source/Grace-worktrees/object-witness-baseline
```

Both commands require Windows and a baseline checkout at the pinned commit. The co-delivered wrapper and project were syntax-checked; the original passing run used the same program with a direct project reference to that baseline. The wrapper is not represented as having run before production coding.

## Limits and production translation

This experiment discards in-memory state over real files and SQLite; it does not certify power loss. The saved-operation shape and caller-held operation ID are a model. `Stream.Null` is a stream-read control, not a manifest-upload test. The production tests in the [validation record](Libraries.Rename-Validation.md) exercise capture after a committed insert with no caller-held ID, a distinct later save, actual manifest uploads, damage to a frozen object reference, and real A/B rename acceptance/replay.

Production uses the configured object directory, existing object name/layout, verified object readers and the typed model in the [type plan](Libraries.Type-Plan.md#issue-1073-local-declarations). Saved-content capture and comparison are streamed. Incoming retained-content downloads still buffer bytes outside this bounded memory change. The earlier [49-case rename experiment](Libraries.Rename-Experiment.md) remains separate modeled evidence for unchanged rename ordering.
