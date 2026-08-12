# Working Directory Update

**Status:** Plan-ready\
**Quality contract:** Product V1\
**Canonical source:** `docs/Working Directory Update.md`\
**Evidence current through:** 2026-08-12, epic base `ff6305130c6bf18c6db0aa1096a251e64a4041d5`

## 1. Outcome

Working Directory Update is the bounded local transaction used by `grace switch` to make a Grace-indexed working
directory and its local SQLite state match one exact selected root. It verifies prepared objects, applies only the
tracked plan, proves the complete final root, commits local completion, and then completes the Branch-specific
finalization.

The first replacement path is deliberately Branch-only. Watch and Connect remain deferred callers; this specification
does not create a generic request shape or a partly active constructor for either one.

## 2. Scope and non-goals

### Required Branch behavior

- `grace switch` routes every supported Reference, SHA-256, and BLAKE3 selector through one private
  `WorkingDirectoryUpdate` module.
- `Reference` selection may change the active Branch only after verified local completion.
- Exact-root `DirectoryVersion` selection keeps the current Branch active. It does not invent a Reference ID.
- One successful pre-switch Save establishes the only accepted current-version baseline for a Save-enabled switch.
- The module derives its repository configuration, local paths, scan input, operation identity, completion facts,
  marker facts, and Branch finalization facts; its immutable admission phase carries and validates the current-state
  binding.
- The result is one of `Unchanged`, `Updated`, `Rejected`, `UpdateIncomplete`, or `FinalizationIncomplete`.

### Deferred callers and work

- Watch replay and finalization are deferred to their named later epic work. They must not add a request constructor,
  callback, or policy branch to the Branch module during #869–#872.
- Connect retrieval, zip staging, target-only `--force`, and cache policy are deferred to their named later epic work.
- Doctor remains the explicit recovery command. #842 consumes the exact pending Branch facts after the Branch sequence;
  it does not make Working Directory Update a generic all-caller engine.

### Product V1 limits

- No filesystem rollback, durable per-file journal, automatic recovery scheduler, migration, compatibility layer, or
  additional durable update state.
- No network access while the local update lease is held.
- No public planning interface, arbitrary path bag, database callback, caller-supplied mutation plan, or generic
  finalizer callback.
- No new public outcome, server route, SDK surface, OpenAPI shape, or generated artifact.

## 3. Current evidence and supersession

| Evidence | Finding | Planning consequence |
| --- | --- | --- |
| Epic base `ff630513` | Branch still owns direct local mutation, status replacement, and Branch publication. | The first replacement path must remove that reachable direct route rather than wrap it. |
| Closed #841 and superseded PR #854 at `bc9bd109` | The broad implementation mixed persistence, runtime, Save admission, finalization, public projection, and the full failure matrix. | Do not restore its generic request or all-caller shapes. Split the sequential Branch work below. |
| Exact-head #854 review | Typed marker results were collapsed, Reference finalization was not repeatable, planning used pre-Save status, topology and cancellation rules were incomplete, and the executable phase matrix was absent. | These are explicit requirements, proof seams, and issue owners below. |
| DEC-001 — typed Branch selection | `Reference` and exact-root `DirectoryVersion` have different Branch semantics. | Persist and reconstruct the typed selection; hashes retain the current Branch. |
| #869–#872 | Four sequential source issues replace #841. | Each issue owns one primary invariant family and a complete vertical proof boundary. |

The superseded commits are evidence and possible narrow test vectors only. They do not establish an implementation
contract, and this document does not describe their runtime shape as accepted.

## 4. Accepted decisions

| ID | Decision | Lasting consequence | Replacement owner |
| --- | --- | --- | --- |
| DEC-001 | Branch selection is typed as `Reference` or exact-root `DirectoryVersion`. | A Reference has its selected Reference ID. A DirectoryVersion has no Reference ID and uses the current Branch as previous and selected Branch. | #869 |
| DEC-002 | The successful pre-switch Save is the sole accepted baseline. | Bind the saved complete status graph by SQLite revision and canonical full-status fingerprint; reread that binding under the lease before planning. | #872 |
| DEC-003 | Marker evidence is a typed disposition, not a Boolean. | Missing, exact, different-operation, malformed or unsupported, unreadable, and exact-cleanup-failed evidence retain distinct behavior. | #869, consumed by #870–#872 |
| DEC-004 | Reference finalization is repeatable from persisted typed facts. | Previous Branch applies once; the exact selected Branch is already applied; any third Branch rejects. | #871 |
| DEC-005 | The plan covers the complete tracked topology. | It creates target directories, removes obsolete tracked empty directories, handles file/directory transitions before mutation, and preserves ignored content. | #870 |
| DEC-006 | The public action token controls only before an irreversible side-effect boundary. | Before the first working-tree mutation, cancellation rejects without mutation or completion. After mutation starts, evidence—not cancellation—determines completion, cleanup, finalization, terminal recording, and outcome. Exact terminal replay is `Unchanged` despite a cancelled invocation token. Explicit finalization retry has the same rule at its first finalization side effect. | #870–#872, #842 |
| DEC-007 | The first deep module has one exact Branch seam. | The caller supplies only sealed `AcceptedBranchPhase`, typed selection, exact target graph, immutable prepared content, and diagnostic correlation. | #870 |
| DEC-008 | Phase proof is split by boundary. | Real filesystem and SQLite tests activate phase and recovery seams; built-command tests cover selector routing and bounded public projection. | #870–#872 |
| DEC-009 | #841 is superseded as an implementation plan. | #868 corrects this contract; #869, #870, #871, and #872 deliver the sequential Branch work. | #868 |

No product decision is open. The rows above refine the accepted Product V1 behavior exposed by the #854 review; they do
not add another state, outcome, recovery mode, or caller.

## 5. Domain language

### Typed Branch selection

`Reference` contains the current Branch, selected Branch, and required selected Reference ID. `DirectoryVersion`
contains the current Branch and exact target root with no Reference ID. Hash prefixes that resolve to the same target
root are the same DirectoryVersion operation; a Reference remains a distinct exact selection.

### Exact target graph

The immutable complete selected directory graph contains the root DirectoryVersion identity, SHA-256 and BLAKE3 roots,
declared files, declared directories including empty directories, and their canonical paths. Root fields alone are not
proof of the graph.

### Accepted Branch phase

Immediately after successful Save or no-Save admission, Branch constructs one opaque, sealed `AcceptedBranchPhase`. It holds
the accepted SQLite revision, canonical fingerprint of the complete current Grace status graph, and the public action
token for that command invocation. It does not hold or expose a mutable status graph, alternate local paths, or a
selected-state reader.

Target resolution and preparation receive that same phase unchanged. `WorkingDirectoryUpdate.run` uses its scalar
revision and fingerprint only to compare its post-lease reread with the accepted baseline before planning. A caller
cannot replace either value or supply a separately assembled prior-status graph after admission.

### Operation and attempt

The deterministic operation binds repository, previous and selected Branch, typed selection and optional Reference,
exact target root, and local-root scope. Diagnostic correlation is not part of operation identity. A random attempt
token identifies one owned marker instance and never substitutes for that identity.

### Local completion and finalization

Local completion is the atomic SQLite commit of verified status, required object metadata, and the pending or terminal
Branch operation. It is the irreversible local point. Finalization occurs only after local completion using the same
persisted typed operation; it changes Branch configuration only for `Reference`.

## 6. Minimal Branch module contract

The private Branch flow has one executable admission phase and one update entry. Admission returns the opaque
`AcceptedBranchPhase` described above immediately after Save or no-Save admission. Resolution and preparation can carry
that phase, but cannot alter its revision, fingerprint, or action token.

The `WorkingDirectoryUpdate.run` entry accepts only the following Branch facts:

1. The sealed `AcceptedBranchPhase`.
2. The typed `Reference` or exact-root `DirectoryVersion` selection.
3. The exact resolved target graph.
4. Immutable prepared content for that graph.
5. Diagnostic correlation.

This is a narrow phase and interface, not a caller-supplied context bag. From the phase and canonical Branch
configuration, the module derives the working root, object root, `.grace` directory, SQLite path, ignore-aware scan
input, local-root scope, operation identity, pending and completion facts, marker disposition, and typed Branch
finalization facts from the same selection, target, canonical configuration, and persisted operation facts. It compares
the phase's accepted revision and fingerprint with its own post-lease reread. A caller cannot provide alternate paths,
a status graph, a selected-state reader, a finalizer or finalizer callback, a progress observer in place of diagnostic
correlation, a filesystem writer, a mutation plan, a database handle, or a generic context bag.

The one action token in `AcceptedBranchPhase` is observed before and during admission and Save, during target resolution
and preparation, while waiting for the lease, during object publication, and immediately before the first
working-tree mutation. Cancellation before that mutation returns `Rejected` without working-tree mutation or a new
completion. Once mutation begins, the token is non-controlling through local completion, the Branch finalization
attempt, exact marker cleanup, and terminal recording.

The module exposes `run` and the internal exact-finalization retry used by #871 and #842. Retry reconstructs the same
typed Branch finalization behavior from the persisted typed operation facts; it cannot accept a separately assembled
finalization tuple or callback. The module does not expose Watch or Connect construction. Any later caller requires its
own accepted design and must not widen this Branch input by adding an arbitrary context record.

## 7. Branch transaction and marker behavior

### Side-effect order

```text
admission and optional current-version Save
→ bind accepted revision plus complete-status fingerprint
→ resolve target and verify prepared content
→ acquire repository/local-root lease
→ reread configuration, SQLite revision, complete status, completion, marker, and relevant filesystem state
→ require the accepted baseline and inspect marker disposition
→ build a fresh tracked plan
→ publish and reverify dual-hash objects
→ final action-token check
→ mutate tracked paths
→ independently verify complete graph and both final root hashes
→ atomically commit local completion
→ clean only exact owned marker evidence
→ Branch finalization
→ mark terminal completion and release the lease
```

Target resolution and prepared-content work finish before the lease while retaining the same immutable
`AcceptedBranchPhase`. Planning always consumes the complete state reread under that lease, never the earlier in-memory
status or a separately assembled prior graph.

### Marker inspection and cleanup table

The pending completion row is the durable local source after local completion. Marker evidence never overrides it and
unknown evidence is never deleted.

| Phase | Evidence or cleanup result | Required Branch behavior | Result when the step cannot continue |
| --- | --- | --- | --- |
| Before local completion | No marker | Create the owned current-attempt marker after fresh revalidation and plan. | Continue normally. |
| Before local completion | Exact matching marker | Adopt only the matching operation, discard its old attempt token, reread all facts, and build a new plan. | Reject if the fresh binding no longer matches. |
| Before local completion | Different operation | Preserve the marker. Do not mutate or replace it. | `Rejected`. |
| Before local completion | Malformed or unsupported marker | Preserve the marker. Do not infer ownership. | `Rejected`. |
| Before local completion | Unreadable marker | Preserve the marker and do not treat absence as proof. | `Rejected`. |
| Before local completion | Exact-marker cleanup fails while handling a pre-commit failure | Preserve the readable exact marker. The update remains non-successful; a later exact operation must revalidate and replan. | `Rejected` before mutation, otherwise `UpdateIncomplete`. |
| After local completion | No marker | Use the matching pending completion row; no working-file mutation is permitted. | Continue only with exact finalization facts. |
| After local completion | Exact matching marker | Clean only that marker, then finalize from the pending row. | Cleanup failure remains pending. |
| After local completion | Different operation | Preserve the marker and do not finalize through ambiguous evidence. | `FinalizationIncomplete`. |
| After local completion | Malformed or unsupported marker | Preserve the marker and do not reduce it to missing. | `FinalizationIncomplete`. |
| After local completion | Unreadable marker | Preserve the evidence and do not continue as if cleanup succeeded. | `FinalizationIncomplete`. |
| After local completion | Exact cleanup failure | Preserve the exact marker and pending completion; do not claim terminal success. | `FinalizationIncomplete` with `grace doctor --repair-local-state`. |

Only missing or exact-cleaned evidence may advance the applicable phase. A different, malformed, unsupported,
unreadable, or cleanup-failed result is retained for the exact replay or Doctor path; it never becomes success through a
Boolean conversion.

### Branch finalization

For a pending `Reference` completion, the module reads the canonical current Branch under the same lease:

| Current Branch state | Finalizer behavior |
| --- | --- |
| Equals the persisted previous Branch | Publish the persisted selected Branch once, then continue to terminal completion. |
| Equals the persisted selected Branch | Treat publication as already complete and continue to terminal completion without working-file mutation. |
| Any third Branch or inconsistent configuration | Preserve pending evidence and reject finalization; do not alter configuration or terminal state. |

For `DirectoryVersion`, previous and selected Branch are the same current Branch. Finalization proves that branch is
still active and never resolves, invents, or publishes a Reference ID.

### Topology and content plan

The fresh plan considers every tracked path and target entry. It removes tracked file blockers and obsolete tracked
empty directories deepest-first, creates required target directories shallowest-first, and applies verified files only
from independently verified objects. File-to-directory and directory-to-file transitions are either planned in that
safe order or rejected before the first mutation. Declared empty directories are materialized and verified as part of
the complete target graph. Ignored content remains untouched; unexpected eligible content rejects before mutation.

Immediately before local completion, a separate verification recomputes the full recursive target graph and both root
hashes from actual filesystem bytes. It proves retained and applied files, directories, empty directories, and path
types independently of the selected root fields.

### Action token and outcomes

The public action token is observed during admission, optional Save, target resolution, preparation, lease waiting,
object publication, and immediately before the first working-tree mutation. Cancellation before that mutation returns
`Rejected` with no working-tree mutation and no new completion. Once mutation starts, cancellation is non-controlling
through local completion, the finalization attempt, exact marker cleanup, and terminal recording. Actual filesystem,
SQLite, marker, and Branch evidence alone determines `Updated`, `UpdateIncomplete`, or `FinalizationIncomplete`.

An exact terminal replay returns `Unchanged` even when the replay invocation's action token is already cancelled. An
explicit finalization retry may observe cancellation only before its first finalization side effect—exact marker cleanup
or Branch publication, whichever occurs first. A cancellation there preserves the pending completion unchanged,
returns `FinalizationIncomplete`, and retains the `grace doctor --repair-local-state` recommendation. After the first
retry side effect begins, cancellation is non-controlling through terminal recording and actual evidence determines the
result.

| Condition | Durable result | Public outcome |
| --- | --- | --- |
| Exact terminal replay, including an already-cancelled invocation token, or an exact no-mutation result | Matching terminal completion or no new mutation | `Unchanged` |
| Cancellation observed before the first working-tree mutation | No new completion and no working-tree mutation | `Rejected` |
| Complete verified root, local completion, exact marker cleanup, finalization, and terminal recording after mutation began | Terminal completion | `Updated` |
| Mutation started but local completion did not commit, regardless of later cancellation | No completion | `UpdateIncomplete` |
| Local completion committed but cleanup, finalization, or terminal recording did not finish, regardless of later cancellation | Pending completion | `FinalizationIncomplete` |
| Explicit finalization retry cancelled before its first finalization side effect | Existing pending completion remains unchanged | `FinalizationIncomplete` |

`FinalizationIncomplete` is nonzero, states that bytes were updated, and recommends
`grace doctor --repair-local-state`. It is never reported as `UpdateIncomplete` after local completion.

## 8. Requirements and proof contract

This is the canonical 17-row ledger for epic #835. Every row has exactly one primary delivery owner. Companion issues
consume, extend, or prove that requirement at their own caller seam; they do not become a second primary owner. #846 is
the final audit owner, not a substitute for a row's delivery owner.

| ID | Requirement | Primary delivery owner | Companion proof, extension, or disposition | #846 final-audit disposition |
| --- | --- | --- | --- | --- |
| REQ-001 | One deep transaction module | #870 | #871 and #872 extend Branch; #843 and #845 add their real callers. | Verify every caller crosses the one module and no bypass remains. |
| REQ-002 | Exact target and operation identity | #869 | #870, #871, #842, #843, and #845 consume persisted identity. | Verify no caller rebuilds a contradictory tuple. |
| REQ-003 | Exact prepared content | #837 | #870 consumes verified Branch objects; #844 owns the deferred Connect zip adapter. | Verify every active adapter satisfies the exact-content contract. |
| REQ-004 | Stable repository/local-root serialization scope | #839 | #870 proves Branch use; #843 and #845 later consume the same scope. | Verify no caller creates an alternate lease scope. |
| REQ-005 | Typed, versioned owned marker evidence | #869 | #870, #871, and #842 consume all dispositions; #843 and #845 defer their caller adoption. | Verify no Boolean replay or cleanup reduction remains. |
| REQ-006 | Fresh planning and local-content safety | #870 | #872 adds the Save baseline; #845 applies the proven plan to Connect. | Verify no direct caller plan or unsafe local mutation remains. |
| REQ-007 | Verified object-first application | #870 | #844 proves Connect zip preparation; #845 consumes the prepared adapter. | Verify active callers copy only verified objects. |
| REQ-008 | Complete final-root verification | #870 | #871, #872, #843, and #845 prove their caller ordering. | Verify no completion can precede independent complete-root proof. |
| REQ-009 | Canonical atomic local completion | #838 | #870, #871, #872, and #845 consume the transaction. | Verify all committed caller facts share the one local completion path. |
| REQ-010 | Bounded pending and terminal completion state | #838 | #871 and #842 prove finalization and recovery retention. | Verify no caller adds a second pending state or retention path. |
| REQ-011 | Idempotent finalization and blocking | #871 | #842 proves Doctor retry; #843 later adds Watch cursor finalization. | Verify finalizers use persisted exact facts and block different work. |
| REQ-012 | Truthful five outcomes and public exit behavior | #871 | #870 proves hash projection; #872, #842, #843, and #845 add their real output cases. | Verify built human/JSON/schema/examples/exits agree. |
| REQ-013 | Deterministic action-token precedence at mutation, replay, and retry boundaries | #870 | #871 proves finalization/retry precedence; #872 adds Save-enabled admission; #842 proves Branch-only Doctor retry; #844 and #845 later carry Connect preparation. | Verify pre-mutation rejection, post-mutation evidence precedence, cancelled terminal replay, and retry's first-side-effect boundary. |
| REQ-014 | Same-operation marker adoption with fresh planning | #869 | #870, #871, and #842 prove Branch and recovery replay. | Verify adoption never resumes an old plan or deletes foreign evidence. |
| REQ-015 | Explicit Doctor recovery without working-file mutation | #842 | No later caller delivery; #846 only audits the completed recovery proof. | Verify exact pending retry and refusal evidence remain current. |
| REQ-016 | Caller-specific ordering | #871 | #872 completes Save ordering; #843 and #845 add Watch and Connect ordering. | Verify each caller has one real ordering path through the transaction. |
| REQ-017 | Current public and contributor documentation | #846 | #868 establishes the Plan-ready contract; #870–#872, #843, #844, and #845 update behavior docs when executable. | Publish the completed 17-row implementation/proof ledger and remove stale planned wording. |

Every phase failure must be deterministically activated somewhere. Module tests use real filesystem and SQLite state for
lease, mutation, local-completion, marker, replay, finalizer, disposal, and release behavior. Built-command tests remain
bounded: they prove selector routing and the corresponding public output rather than trying to inject every internal
phase failure through the command process. #870 proves deterministic construction of `AcceptedBranchPhase` for no-Save
admission. #872 proves the Save-enabled built command, including a deterministic tracked edit made after the phase is
captured while target preparation is still in progress; the post-lease revision/fingerprint comparison must reject
before target mutation.

## 9. Propagation and traceability

| Surface | Disposition | Owner and proof |
| --- | --- | --- |
| Private Branch selection, pending row, and marker records | Updated with typed selection and explicit dispositions. | #869; contract, SQLite, and marker tests. |
| `WorkingDirectoryUpdate` Branch module | Updated as one deep Branch-only transaction with immutable admission phase and action token. | #870; construction and real filesystem/SQLite phase matrix. |
| `grace switch` Reference and hash dispatch | Updated; no direct mutation/status/finalization bypass. | #870–#872; built selector fixtures. |
| Local schema | Clean development replacement for the typed pending selection. | #869; schema reset and reopen tests. |
| Branch output, schema, examples, and current behavior docs | Updated only as the replacement selectors and outcomes become executable. | #870–#872; built human/JSON/exit checks and MarkdownLint. |
| Doctor `--repair-local-state` | Deferred until exact pending Branch facts exist. | #842; filesystem-free retry proof. |
| Watch replay and cursor finalization | Deferred; no Branch-side Watch constructor or policy is added. | #843; ordered replay and built Watch proof. |
| Connect zip preparation | Deferred; no Branch-side zip adapter or target-conflict policy is added. | #844; exact staged-content proof. |
| Connect retrieval and cursor completion | Deferred; no Branch-side Connect constructor or policy is added. | #845; retrieval, completion, and output proof. |
| Retired-path removal and completed ledger | Deferred final audit; it adds no new behavior. | #846; source-boundary, built-command, documentation, and canonical 17-row audit. |
| Server, SDK, OpenAPI, and generated artifacts | Not applicable: this local Branch contract changes no wire shape. | Scoped diff and review. |
| This specification and ADR 0011 | Updated by #868 before source work resumes. | MarkdownLint, local links, traceability and lifecycle audit. |

## 10. Replacement implementation handoff

The replacement sequence is intentionally serial because the modules, Branch command, local completion, and proof
fixtures share a high-risk write set.

1. **#868 — contract correction:** make this specification and ADR Plan-ready; remove the superseded all-caller request
   model and assign every requirement to one replacement owner.
2. **#869 — typed persistence and markers:** deliver schema-11 typed selection reconstruction and explicit marker
   inspection and cleanup results. Stop before runtime or caller behavior.
3. **#870 — exact-root hash tracer:** deliver both hash selectors through the real Branch transaction, with current
   Branch retention, complete planning, cancellation, replay, and real phase proof.
4. **#871 — Reference finalization:** extend the proven tracer with Reference publication, repeatable finalization,
   pending recovery behavior, and the remaining public outcome contract.
5. **#872 — Save-enabled admission:** bind the successful Save graph as the accepted baseline and complete the
   Save-enabled built-command regression.

Issue #842 follows #871 and completes the exact Doctor recovery path after the #872 Branch checkpoint. Watch and Connect are
not admissible implementation work in this sequence.

## 11. Completion and review gate

The Branch checkpoint is complete only when the source issue owning each requirement has a current exact-head review
and `Validate` proof. A green build, an output registry entry, or a private helper is not a substitute for the required
real module or built-command evidence.

Before a source issue is reviewed, its self-review records one status for every requirement it owns: implemented and
proven, implemented but proof incomplete, waived with reason, out of scope with reason, or not applicable with reason.
No status may hide a failure seam in helper-only proof. The final traceability audit confirms that every row above has
one owner, no Watch or Connect behavior slipped into the Branch interface, and no superseded generic request remains.

The remaining Product V1 risk is intentionally bounded: interruption after working-tree mutation but before local
completion can leave bytes that require exact revalidation. Grace preserves evidence rather than guessing, rolling back,
or introducing a broader recovery system.
