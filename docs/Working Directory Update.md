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
- The module derives its repository configuration, local paths, scan input, current-state binding, operation identity,
  completion facts, marker facts, and Branch finalization facts.
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
| Accepted D1 | `Reference` and exact-root `DirectoryVersion` have different Branch semantics. | Persist and reconstruct the typed selection; hashes retain the current Branch. |
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
| DEC-006 | The public action token remains attached until mutation begins. | Admission, Save, resolution, preparation, lease wait, object publication, and the final pre-mutation boundary observe it; only post-mutation cancellation is deferred. | #870, #872 |
| DEC-007 | The first deep module has a minimal Branch seam. | The caller supplies only typed selection, exact target graph, verified prepared content, and diagnostic correlation. | #870 |
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

### Accepted local baseline

The immutable binding created by the successful current-version Save, or by supported no-Save admission, contains the
SQLite revision and canonical fingerprint of the complete current Grace status graph. It is not a caller-supplied
prior-status graph. The post-lease reread must equal it before a plan can be built.

### Operation and attempt

The deterministic operation binds repository, previous and selected Branch, typed selection and optional Reference,
exact target root, and local-root scope. Diagnostic correlation is not part of operation identity. A random attempt
token identifies one owned marker instance and never substitutes for that identity.

### Local completion and finalization

Local completion is the atomic SQLite commit of verified status, required object metadata, and the pending or terminal
Branch operation. It is the irreversible local point. Finalization occurs only after local completion using the same
persisted typed operation; it changes Branch configuration only for `Reference`.

## 6. Minimal Branch module contract

The private Branch entry to `WorkingDirectoryUpdate.run` accepts exactly four caller-provided facts:

1. The typed `Reference` or `DirectoryVersion` selection.
2. The exact resolved target graph.
3. Verified prepared content for that graph.
4. Diagnostic correlation.

From these facts and canonical Branch configuration, the module derives the working root, object root, `.grace`
directory, SQLite path, ignore-aware scan input, local-root scope, accepted revision and complete-status fingerprint,
operation identity, pending and completion facts, marker disposition, and Branch finalization facts. A caller cannot
provide alternate paths, a selected-state reader, an old status graph, a finalizer, a filesystem writer, a mutation
plan, or a database handle.

The module exposes `run` and the internal exact-finalization retry used by #871 and #842. It does not expose Watch or
Connect construction. Any later caller requires its own accepted design and must not widen this Branch input by adding
an arbitrary context record.

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

Target resolution and prepared-content work finish before the lease. Planning always consumes the complete state
reread under that lease, never the earlier in-memory status or a separately assembled prior graph.

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
object publication, and immediately before the first working-tree mutation. Once mutation starts, cancellation is
deferred until verified local completion or an unavoidable incomplete failure; it does not interrupt the local
transaction half way through. It may apply again to finalization.

| Condition | Durable result | Public outcome |
| --- | --- | --- |
| Existing matching terminal completion or exact no-mutation result | Matching terminal completion | `Unchanged` |
| Complete verified root, local completion, marker cleanup, and finalization | Terminal completion | `Updated` |
| Stale baseline, foreign or damaged pre-completion evidence, conflict, or cancellation before mutation | No new completion | `Rejected` |
| Mutation started but local completion did not commit | No completion | `UpdateIncomplete` |
| Local completion committed but cleanup, finalization, or terminal completion did not finish | Pending completion | `FinalizationIncomplete` |

`FinalizationIncomplete` is nonzero, states that bytes were updated, and recommends
`grace doctor --repair-local-state`. It is never reported as `UpdateIncomplete` after local completion.

## 8. Requirements and proof contract

| ID | Requirement | Implementation owner | Independent proof |
| --- | --- | --- | --- |
| REQ-001 | Persist and reconstruct exact typed `Reference` and `DirectoryVersion` selection; a hash retains the current Branch and exact root. | #869 | Real SQLite close/reopen for both kinds; equivalent SHA-256 and BLAKE3 prefixes map to one exact-root operation. |
| REQ-002 | Reject impossible selector combinations and preserve exact marker dispositions. | #869 | Real marker files cover missing, exact, different, malformed or unsupported, unreadable, and portable exact-cleanup failure. |
| REQ-003 | The Branch module receives only selection, exact target graph, prepared content, and correlation. | #870 | Construction tests show config, paths, scan, revision, fingerprint, operation, completion, marker, and finalization facts are derived. |
| REQ-004 | Plan only after the post-lease complete-status reread equals the accepted baseline. | #870, #872 | Deterministically change config, revision, fingerprint, relevant file, target, or pending row while waiting; each rejects before mutation. |
| REQ-005 | Publish and reverify objects before copying tracked working files. | #870 | Real object corruption and final-tree mismatch cases leave no local completion. |
| REQ-006 | Plan and verify files, directories, empty directories, and path-type transitions while preserving ignored content. | #870 | Real filesystem nested addition/removal, empty directory, file/directory, directory/file, ignored-content, and unexpected-eligible cases. |
| REQ-007 | Observe the action token through every pre-mutation stage and defer it after mutation begins. | #870, #872 | Deterministic cancellation at lease wait, object publication, final pre-mutation, and post-mutation boundaries. |
| REQ-008 | Reference finalization accepts previous and exact-selected states, but rejects a third state. | #871 | Fail terminal completion after successful publication, reopen, retry without file changes, and assert all three Branch states. |
| REQ-009 | Cleanup and finalization after local completion remain pending on every non-success disposition. | #871 | Real completion/reopen cases for foreign, malformed, unsupported, unreadable, finalizer, and exact-cleanup failures. |
| REQ-010 | A successful pre-switch Save supplies the only accepted current baseline. | #872 | Built Save-enabled switch proves saved graph, revision, and full-status fingerprint are reread under lease; post-Save drift rejects. |
| REQ-011 | All supported selectors use the transaction and project five truthful outcomes. | #870–#872 | Built `grace switch` fixtures cover Reference, SHA-256, BLAKE3, representative human/JSON/exits, and repair guidance. |
| REQ-012 | Recovery is exact and does not mutate working files. | #842 | Built Doctor retry proves typed pending facts and leaves working bytes and timestamps unchanged. |

Every phase failure must be deterministically activated somewhere. Module tests use real filesystem and SQLite state for
lease, mutation, local-completion, marker, replay, finalizer, disposal, and release behavior. Built-command tests remain
bounded: they prove selector routing and the corresponding public output rather than trying to inject every internal
phase failure through the command process.

## 9. Propagation and traceability

| Surface | Disposition | Owner and proof |
| --- | --- | --- |
| Private Branch selection, pending row, and marker records | Updated with typed selection and explicit dispositions. | #869; contract, SQLite, and marker tests. |
| `WorkingDirectoryUpdate` Branch module | Updated as one deep Branch-only transaction. | #870; real filesystem/SQLite phase matrix. |
| `grace switch` Reference and hash dispatch | Updated; no direct mutation/status/finalization bypass. | #870–#872; built selector fixtures. |
| Local schema | Clean development replacement for the typed pending selection. | #869; schema reset and reopen tests. |
| Branch output, schema, examples, and current behavior docs | Updated only as the replacement selectors and outcomes become executable. | #870–#872; built human/JSON/exit checks and MarkdownLint. |
| Doctor `--repair-local-state` | Deferred until exact pending Branch facts exist. | #842; filesystem-free retry proof. |
| Watch and Connect | Deferred; no Branch-side request construction or policy is added. | Later named epic work; scoped-diff review. |
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
