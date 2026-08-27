# Issue #1038 test plan

## Domain and repository contract

- Add focused Types tests for default values, string enum values, optional-field combinations, normalized root/path rules, bounds, exact mutation field combinations, and result serialization.
- Add repository tests for the persisted empty configuration and exact-version root transitions, including overlap, duplicate, unsupported path, root limit, stale policy, and no-effect rejection cases.

## Authorization and server behavior

- Add authorization tests for reader, writer, administrator, broad-role composition, repository-write non-implication, cross-repository identifiers, and hidden-resource behavior.
- Add unit tests for deterministic request hashing, key/segment bounds, admission decisions, replay, stale matrices, projection watermark checks, idempotent repair, and content-free status.
- Add Aspire-backed tests for the six containers, canonical-mutation-first ordering, failure at each publication boundary, restart repair, bootstrap, deltas, receipts, content prepare/read, routes, and immutable residue after rejection.

## Public clients and category boundary

- Add SDK tests for every remote facade method and structured outcome.
- Add CLI tests for root list/get/add/remove, help, human/JSON output, validation, and inert schema/examples while proving local participation commands are absent.
- Add generated-contract freshness and route-coverage checks for static OpenAPI, TypeScript, Python, Rust, and .NET metadata.
- Add focused Save and Branch/WDU tests proving configured synchronized roots are excluded without changing WDU caller, target, or completion semantics.

## Completion review

- Re-read every new assertion against the Issue #1038 acceptance and failure matrix.
- Record exact test names and successful commands in `.testagent/status.md`.
- Format touched F# files, run focused Release builds/tests and generation/Markdown checks, then run `git diff --check`.
