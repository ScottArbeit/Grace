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
| FileVersion, LocalFileVersion, DirectoryVersion, LocalDirectoryVersion, GraceStatus, ReferenceDto, branch/reference commands, and branch/reference events carry explicit `Blake3Hash` and `Sha256Hash` where version lookup or display requires them. | Type, actor, CLI, local-state, SDK, and OpenAPI files in the `origin/main...HEAD` diff include dual-hash fields and command propagation. | Satisfied |
| File BLAKE3 and SHA-256 are byte-only. | `Services.Shared.fs`, file hash helper tests, current-state capture tests, and this doc refresh keep file hashes byte-only. | Satisfied |
| DirectoryVersion BLAKE3 and SHA-256 use the formal `grace.directory-version.v1` preimage with an algorithm discriminator, base64-encoded directory and child paths, `child-count`, indexed child rows, child kind, child size for every entry, same-algorithm child hashes, and newline delimiters, sorted by normalized child path and then child kind. | `DirectoryVersionPreimage.Shared.Tests.fs`, `Services.Shared.fs`, actor/server-unit promotion tests, and updated docs describe and prove the formal preimage. | Satisfied |
| BLAKE3 lookup exists wherever SHA-256 version lookup exists. | Branch, reference, diff, and directory server/SDK/CLI/OpenAPI surfaces include BLAKE3 lookup peers for SHA-256 lookup paths. | Satisfied |
| `--blake3-hash` resolves repository version graph objects only, not arbitrary CAS objects. | Server/unit and CLI lookup tests cover version-hash prefix resolution; CAS docs keep `FileContentHash`, `ChunkAddress`, `ContentBlockAddress`, and `ManifestAddress` separate from version hashes. | Satisfied |
| Prefix lookup distinguishes zero, one, and multiple matches for both algorithms. | `Services.VersionHashPrefixResolution.Tests.fs`, lookup route tests, and CLI parsing tests cover zero/one/multiple prefix outcomes. | Satisfied |
| Human CLI output shows BLAKE3 prefixes by default, shows SHA-256 only with `--show-sha256`, and shows full values with `--full-hashes`. | CLI command and text output changes plus CLI tests cover human output defaults and opt-ins. | Satisfied |
| JSON output includes full explicit `Blake3Hash` and `Sha256Hash` values. | `CommandOutputContract.CLI.fs`, CLI JSON tests, `docs/Machine-readable CLI output.md`, OpenAPI, and generated SDK artifacts expose full dual-hash JSON values. | Satisfied |
| `--full-sha` is deprecated immediately, with tested behavior. | CLI option parsing and command tests cover the deprecated alias behavior. | Satisfied |
| Local SQLite state stores BLAKE3 and SHA-256 or clearly resets incompatible old state. | Local-state DB code and tests persist dual hashes; docs emphasize no production-data migration promise. | Satisfied |
| OpenAPI, SDK, docs, ADR, tests, and final audit are updated. | OpenAPI source/bundles/proof manifest, generated SDK matrix artifacts, ADR 0006, hash docs, data-type docs, and this final audit are updated. | Satisfied |
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

No non-blocking cleanup issue is required from this audit. The observed gaps were stale documentation wording, and this
slice updates those docs directly.

## Release-Candidate Conclusion

Epic #343 satisfies its acceptance criteria on the audited branch. The remaining release work belongs to orchestrator
coordination: open or update the final ready-for-review PR to `main`, record validation and review state on that PR,
monitor Codex Code Review Bot on the latest head, and stop for maintainer manual review rather than automatically
merging the epic branch.
