# Issue #1038 Libraries test plan

## Domain and repository contract

- Rename and update focused Types tests for default values, string enum values, optional-field combinations, normalized Library path rules, bounds, exact change field combinations, `LibraryCatalogDto.Libraries`, `LibraryChangePageDto.Changes`, and result serialization.
- Update repository tests for the persisted empty Library catalog and exact-version Library transitions, including overlap, duplicate, unsupported path, Library limit, stale policy, and no-effect rejection cases.

## Authorization and server behavior

- Rename and update authorization tests for Library reader, writer, administrator, broad-role composition, repository-write non-implication, cross-repository identifiers, and hidden-resource behavior.
- Add unit tests for the six exact partition-key definitions, exact-partition routing, prohibited-query negatives, deterministic request hashing, change/segment bounds, rejection before current projection document 100,001, admission decisions, replay, stale matrices, projection watermark checks, idempotent repair, and content-free status.
- Add actor and interleaving proof that `RepositoryLibraryActor` owns catalog and accepted change serialization, Repository/Save/Reference/Branch call it one way, and no Library-owned path calls back into `RepositoryActor`.
- Retain the accepted hosted-test gap unless a focused existing Aspire fixture can truthfully cover the six containers, canonical-change-first ordering, restart repair, bootstrap, changes, receipts, content prepare/read, routes, and immutable residue without broadening this run.

## Public clients and category boundary

- Rename and update SDK tests for every `Grace.SDK.Libraries` facade method and structured outcome.
- Rename and update CLI tests for `grace library list|get|add|remove`, help, human/JSON output, validation, and inert schema/examples while proving top-level `sync` aliases and local participation commands are absent.
- Add generated-contract freshness and route-coverage checks for static OpenAPI, TypeScript, Python, Rust, and .NET metadata.
- Add focused Save and Branch/WDU tests proving configured Libraries are excluded without changing WDU caller, target, or completion semantics.
- Run stale-name negatives over current source, static OpenAPI, generated clients, tests, and docs, with unrelated-domain and immutable historical exceptions classified explicitly.

## Completion review

- Re-read every new assertion against the Issue #1038 acceptance and failure matrix.
- Record exact test names and successful commands in `.testagent/status.md`.
- Format touched F# files, run focused Release builds/tests and generation/Markdown checks, then run `git diff --check`.
