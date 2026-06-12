# BLAKE3 and SHA-256 Version Hash Final Audit

Issue #364 performed the final audit for epic #343 on branch
`agent/364-i21-final-audit-release-candidate`, based on
`origin/epic/343-blake3-sha256-version-hashes`.

Grace is not in production. This audit treats the BLAKE3 plus SHA-256 behavior as the current desired contract and does
not add migration, import, preservation, or grandfathering work for imaginary old production data.

## Branch Evidence

- `git fetch origin` succeeded before the audit.
- `git rev-list --left-right --count origin/epic/343-blake3-sha256-version-hashes...HEAD` returned `0 0` before this
  issue's audit commit, proving the worker branch started exactly at the epic branch head.
- `git rev-list --left-right --count origin/main...HEAD` returned `0 86`, proving the epic branch was 86 commits ahead
  of `origin/main` at the audit point.
- Issue #343 listed child issues #344 through #363 complete and #364 open for the final audit.
- Issue #343 records native relationship verification from 2026-06-10: 21 child issues, each with parent #343.
- PR review history for #365, #369, #371, and #373 was inspected. PR #373 was closed without merge after review churn;
  the restarted work landed later in the epic branch and is included in this branch audit.

## Acceptance Matrix

| Epic acceptance criterion | Evidence on this branch | Status |
| --- | --- | --- |
| FileVersion, LocalFileVersion, DirectoryVersion, LocalDirectoryVersion, GraceStatus, ReferenceDto, branch/reference commands, and branch/reference events carry explicit `Blake3Hash` and `Sha256Hash` where version lookup or display requires them. | Type, actor, CLI, local-state, SDK, and most OpenAPI files in the `origin/main...HEAD` diff include dual-hash fields and command propagation. Recheck found `GetBranchParameters` and `GetReferenceParameters` still inherit the SHA-only `BranchQueryParameters` schema, so the OpenAPI branch/reference lookup contract is not fully complete. | Deferred follow-up |
| File BLAKE3 and SHA-256 are byte-only. | `Services.Shared.fs`, file hash helper tests, current-state capture tests, and this doc refresh keep file hashes byte-only. | Satisfied |
| DirectoryVersion BLAKE3 and SHA-256 use the formal `grace.directory-version.v1` preimage with an algorithm discriminator, base64-encoded directory and child paths, `child-count`, indexed child rows, child kind, child size for every entry, same-algorithm child hashes, and newline delimiters, sorted by normalized child path and then child kind. | `DirectoryVersionPreimage.Shared.Tests.fs`, `Services.Shared.fs`, actor/server-unit promotion tests, and updated docs describe and prove the formal preimage. | Satisfied |
| BLAKE3 lookup exists wherever SHA-256 version lookup exists. | Diff and directory lookup routes have BLAKE3 peers, and branch/reference runtime surfaces include BLAKE3 lookup support. Static OpenAPI still models `/branch/get`, `/branch/getByName`, and `/branch/getReference` through SHA-only query parameters, so lookup parity is not fully represented in the published API contract. | Deferred follow-up |
| `--blake3-hash` resolves repository version graph objects only, not arbitrary CAS objects. | Server/unit and CLI lookup tests cover version-hash prefix resolution; CAS docs keep `FileContentHash`, `ChunkAddress`, `ContentBlockAddress`, and `ManifestAddress` separate from version hashes. | Satisfied |
| Prefix lookup distinguishes zero, one, and multiple matches for both algorithms. | `Services.VersionHashPrefixResolution.Tests.fs`, lookup route tests, and CLI parsing tests cover zero/one/multiple prefix outcomes. | Satisfied |
| Human CLI output shows BLAKE3 prefixes by default, shows SHA-256 only with `--show-sha256`, and shows full values with `--full-hashes`. | Most branch/reference CLI text output follows the new default and opt-in behavior. Recheck found `grace repository init` still prints `Root SHA-256 hash`, and maintenance root summaries still print root SHA-256 by default. | Deferred follow-up |
| JSON output includes full explicit `Blake3Hash` and `Sha256Hash` values. | Most JSON output contracts expose full dual-hash values. Recheck found `LocalOutputDto.RepositoryInitDto` still exposes only `RootSha256Hash`, while the repository init command returns no root BLAKE3 JSON property. | Deferred follow-up |
| `--full-sha` is deprecated immediately, with tested behavior. | CLI option parsing and command tests cover the deprecated alias behavior. | Satisfied |
| Local SQLite state stores BLAKE3 and SHA-256 or clearly resets incompatible old state. | Local-state DB code and tests persist dual hashes; docs emphasize no production-data migration promise. | Satisfied |
| OpenAPI, SDK, docs, ADR, tests, and final audit are updated. | Generated SDK matrix artifacts, ADR 0006, hash docs, data-type docs, and this final audit are updated. Static and bundled OpenAPI still need the branch/reference BLAKE3 query follow-up described below. | Deferred follow-up |
| Non-version SHA-256 uses are unchanged unless explicitly owned by a future issue. | Authorization, webhook/payload, dedupe, and non-version SHA-256 regression tests remain intentionally SHA-256; no follow-up is needed from this audit. | Satisfied |

## Documentation Refresh

This audit refreshed docs that still sounded like pre-implementation planning:

- `README.md` now treats multi-hash semantics as a current hardening area rather than future BLAKE3 introduction.
- `CONTEXT.md` now describes version hashes as current BLAKE3 plus retained SHA-256 lookup and display values, not only
  an ADR target model.
- `docs/Data types in Grace.md` now lists both `Blake3Hash` and `Sha256Hash` for DirectoryVersion, Reference, and
  FileVersion.
- `docs/How Grace computes the SHA-256 value.md` now describes the current dual-hash behavior and formal directory
  preimage.
- `docs/adr/0006-blake3-and-sha256-version-hashes.md` now records epic #343 as implemented and avoids production-data
  migration language.

SDK/OpenAPI architecture docs and generated SDK docs already reflected the generated dual-hash contract closely enough
for this audit. This slice did not hand-edit generated artifacts.

## Follow-Up Decision

The audit found two non-blocking contract cleanup items that should become follow-up issues. This audit does not change
CLI, OpenAPI, generated artifacts, or SDK code directly because each item touches a public contract surface and needs
focused tests or proof regeneration beyond final-audit documentation cleanup.

### Follow-up issue draft: Align repository and maintenance root hash CLI output with BLAKE3 defaults

```markdown
Objective:
- Make local root-hash CLI output follow the epic #343 display contract: BLAKE3 prefixes by default, SHA-256 only with
  explicit opt-in, and full dual-hash values in machine-readable JSON where root version hashes are reported.

Context and evidence:
- PR #393 final-audit recheck found `grace repository init` still prints `Root SHA-256 hash` in
  `src/Grace.CLI/Command/Repository.CLI.fs`.
- `LocalOutputDto.RepositoryInitDto` in `src/Grace.CLI/Command/Common.CLI.fs` still exposes only `RootSha256Hash`.
- Maintenance root summaries in `src/Grace.CLI/Command/Maintenance.CLI.fs` still print root SHA-256 by default, even
  though maintenance JSON stats already include both `RootSha256Hash` and `RootBlake3Hash`.

Owned paths:
- `src/Grace.CLI/Command/Common.CLI.fs`
- `src/Grace.CLI/Command/Repository.CLI.fs`
- `src/Grace.CLI/Command/Maintenance.CLI.fs`
- `src/Grace.CLI/CommandOutputContract.CLI.fs`
- `src/Grace.CLI.Tests`
- `docs/Machine-readable CLI output.md`, if command output docs need contract updates.

Validation:
- Run targeted Fantomas on touched F# files.
- Run focused `Grace.CLI.Tests` coverage for repository init and maintenance output contracts.
- Run `pwsh ./scripts/validate.ps1 -Fast` unless the focused gate is explicitly accepted as sufficient.
- Run `git diff --check`.
```

### Follow-up issue draft: Add BLAKE3 query parity to branch/reference OpenAPI schemas

```markdown
Objective:
- Ensure static and bundled OpenAPI represent BLAKE3 lookup everywhere SHA-256 branch/reference version lookup exists.

Context and evidence:
- PR #393 final-audit recheck found `GetBranchParameters` and `GetReferenceParameters` in
  `src/OpenAPI/Branch.Components.OpenAPI.yaml` inherit `BranchQueryParameters`, which has `Sha256Hash` and
  `ReferenceId` but no `Blake3Hash`.
- `BranchHashQueryParameters` already defines a BLAKE3 peer, but the get-branch and get-reference schemas do not use
  it.
- Bundled OpenAPI files mirror the same SHA-only inheritance, so source schema, bundled output, proof checks, and any
  generated artifacts should be updated together.

Owned paths:
- `src/OpenAPI/Branch.Components.OpenAPI.yaml`
- `src/OpenAPI/Grace.OpenAPI.yaml`
- `src/OpenAPI/Grace.OpenAPI.3.1.2.yaml`
- `src/OpenAPI/prove-openapi.ps1`
- Generated SDK/OpenAPI artifacts or proof manifests required by the repo's OpenAPI workflow.

Validation:
- Run the repo OpenAPI proof/generation command documented for the current branch.
- Run `pwsh ./scripts/validate.ps1 -Fast` if generated client or shared contract artifacts change.
- Run `git diff --check`.
```

## Release-Candidate Conclusion

Epic #343 is close to release-candidate shape, but this audit no longer marks every acceptance criterion fully
satisfied. The remaining release decision belongs to orchestrator and maintainer coordination: either schedule the two
follow-up issues above before the final `main` release PR, or explicitly accept them as deferred non-blocking cleanup in
the release-candidate PR record. In either path, the orchestrator should open or update the final ready-for-review PR to
`main`, record validation and review state on that PR, monitor Codex Code Review Bot on the latest head, and stop for
maintainer manual review rather than automatically merging the epic branch.
