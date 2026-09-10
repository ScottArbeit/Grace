# Library additive catalog-adoption production acceptance

**Status: required tests are mapped; production implementation and validation are pending.** No adoption command is delivered by this document. The [accepted design](../Libraries.Design.md#additive-catalog-adoption-accepted-not-implemented), [type plan](Libraries.Type-Plan.md#additive-catalog-adoption-propagation-accepted-not-implemented) and [experiment](Libraries.Catalog-Adoption-Experiment.md) make LIB-019 through LIB-021 Plan-ready for implementation planning. Each tracked implementation still needs a fresh base, scoped readiness gate and delivery review.

## Requirement mapping

| Requirement | Production seam | Required tests before delivery | Existing evidence and remaining gap |
| --- | --- | --- | --- |
| LIB-019: catalog-only adoption | `Command/Library.CLI.fs`; `LibrarySynchronization` adoption; `LibraryLocalState` exact-row transaction; existing status/output registry | Invoke the actual command on an onboarded paused copy. Assert catalog change with identical identity, cursor, epoch, pause, items, operation JSON/indexes and object/file bytes. Fail before/inside/after commit, reopen, retry and repeat successfully. Check human/JSON results and command schema/help. | Three real SQLite interruption groups and actual row/item/operation/object reads passed in modeled adoption. Production command, cancellation wiring and output are not implemented. |
| LIB-020: refusal and retained input | Existing root lease, repository/operation reads, materialized ancestry and Windows physical checks; Watch classifier | Positive clean state derived from durable materialization; dirty/zero-byte/untracked inputs and occupied/reparse added roots. Refuse inactive/unpaused/incomplete participation, any unfinished saved/directory/rename/incoming/baseline work, partial paging, unsupported catalog delta, unavailable feed, changed epoch/rebaseline and changed inputs before commit. Cancel an actual command waiting on the lease. Queue actual Watch classification behind adoption and assert no paused effects. | Nineteen exclusion groups, canceled real lease and queued actual classifier passed. Catalog/epoch/rebaseline negatives include explicit injections; expected clean bytes came from a verified fixture snapshot. Production must establish all permitted input families and root normalization/ancestry through its own entrypoints. |
| LIB-021: complete ordered replay | Existing incoming operation preparation, history admissibility check, `LibraryLocalState` completion and finite synchronization | Actual A/B command/HTTP tracer below; reject unsupported accepted history before capture/publication; retain local frozen-request restriction. Inject failure after incoming publication before item/operation/cursor commit and restart a new CLI process. Assert exact original accepted changes, complete old-root backlog, new-root bytes, retained item/content identity, and no duplicate upload or completed-file rewrite. | Real CLI/HTTP/content replay passed with two guards changed only in the disposable source copy. A real completion-trigger failure and new CLI restart passed. Production must translate the preconditions and guard changes, not copy a fixture-only catalog write. |

The unfinished-work condition applies to all nonterminal operation families, not only those seeded in the experiment. The metadata update never transforms a frozen request, marks unfinished work terminal or uses a new baseline to bypass missing history. Refusal does not authorize cleanup or filesystem changes.

## Smallest value-bearing tracer

1. Start two authorized Windows copies already onboarded to one root. Synchronize a nonempty file into both.
1. Publish another original-root edit in A; leave it pending for B. Pause both. Create the added ordinary empty local directory in each copy.
1. Use the existing administrator catalog-add operation to create one direct successor. Invoke the new local adoption command in A. Assert unchanged cursor/epoch and retained pause, then explicitly resume.
1. Publish a nonempty file under the added root from A. Invoke the actual adoption command in B, preserving its old cursor and original materialized base, then resume.
1. B must apply the old-catalog backlog in the unchanged root and the new-root content through genuine feed predecessors. Restart/repeat after completion and assert no extra logical submission/upload or completed-file rewrite.

Use actual production command entrypoints, authenticated catalog/feed calls, retained immutable content and real SQLite. A fixture that replaces adoption with raw SQL can remain a focused algorithm test but cannot satisfy this production tracer.

## Changed inputs and failure behavior

Before adoption, local repository/operation/materialized state and real disk supply expected local facts; current catalog/feed responses supply the selected remote facts. Reread them under the existing root lease immediately before the catalog transaction. SQLite compares the complete expected repository row. A changed row, operation set, root/disk observation or catalog prevents the update. The remote observation retains the accepted point-in-time guarantee.

The after-commit/lost-response case must distinguish committed adoption from failure before commit by reading durable state. The operation remains paused; retrying the same current selection does not apply feed or advance the cursor. Resume remains the delivered separate active-before-run operation, so a failed resume can remain active/blocked with retained work.

For replay, demonstrate both the old-catalog incoming positive case and the unchanged local-request negative case. An unsupported accepted catalog must be rejected before capture or publication. An exact cursor is retained until genuine item application completes; a successful metadata comparison alone cannot advance it.

The experiment's Save Reference/catalog ordering results preserve existing behavior and the documented point-in-time race. They are not permission to require a new cross-domain reservation. A full running Watch adoption/restart test is a production integration obligation if the implementation changes that lifetime; the existing queued-classifier experiment covers only its unchanged local boundary.

## Delivery record to fill after implementation

At implementation time, map each row to the actual named tests, commands and revision. Record focused failures/passes, explicitly modeled inputs, any unexecuted cases, independent review, required final-head GitHub Validate and the approved delivery revision. Do not replace this pending status with the experiment's 4/4 count.

One Product V1 Tier 2 mainline slice is recommended; the existing CLI/local-state/Watch and shared test files belong to one implementation owner. No implementation child or target revision exists yet. A new table/lifecycle, rewritten frozen input, cursor skipping, required rebaseline, broader root transition or stronger cross-domain guarantee returns the work to an owner decision.
