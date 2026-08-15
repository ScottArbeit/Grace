# Grace Cache implementation audit

## Purpose

This audit distinguishes independently proven foundations from active Product V1 behavior after the Factory V2 reset.
It is evidence, not a second specification. `docs/Grace Cache.md` is the canonical product contract.

Status vocabulary:

- **Proven foundation:** merged or otherwise reproducibly demonstrated and eligible for salvage after current-head audit.
- **Calibration requirement:** required by GC-CAL-01 #965.
- **Deferred:** intentionally absent from the calibration increment.
- **Historical evidence:** useful design, code, fixture, test, or review material that is not current authority.
- **Unverified:** requires exact branch and commit inspection in GC-CAL-00.

## Factory V2 baseline

- Quality contract: Product V1 internal tracer slice for GC-CAL-01 #965.
- Active production outcome: one immutable `DirectoryVersionZip` reaches artifact-local `Complete` only after exact
  final-file and SQLite agreement, then classifies correctly after process reopen.
- Active sequence: completed Discovery issue #964, followed by implementation issue #965 only.
- Agent topology: one short-lived controller per issue or pull request, one issue-owner implementation worker, optionally one independent reviewer, no child
  spawning, maximum two live agents.
- Review: one R1 Discovery Review frozen ledger; when R1 accepts repairs, one repair pass and one targeted R2 Closure Review.
- Process rules are frozen for the run.

## Current tracker disposition

| Tracker item | Reset disposition |
| --- | --- |
| #597 | Keep as product parent; replace body with Factory V2 reset contract. |
| #601 and #602 | Historical mini-epics; pause or close as superseded by the calibration shape. |
| #623 | Proven-foundation candidate; schema and tests require exact current-branch audit. |
| #624 | Superseded design evidence; its supported filesystem cases feed #965 without preserving its old issue shape. |
| #625 | Superseded horizontal endpoint issue; accepted grant-validation details remain evidence. |
| #626 | Superseded horizontal source-authority issue; exact revalidation rules remain evidence. |
| #627 | Superseded horizontal fill issue; failure cases remain evidence. |
| #857 and #958 | Deferred from calibration. Do not continue enrollment or liveness implementation. |
| #628 through #637 | Deferred pending a successful tracer and new capability decisions. |
| #554 and #835 | No dependency for GC-CAL-01. Keep their production work paused during calibration. |

## GC-CAL-00 artifact algorithm calibration

- **Status classification:** implemented and proven as disposable Discovery evidence; not production code.
- **Issue:** [#964](https://github.com/ScottArbeit/Grace/issues/964).
- **Base:** `origin/epic/601-grace-cache-runtime-store` at
  `8852de4665372f438075fc6952410ea02902f8e6`.
- **Result:** one Linux x64 `DirectoryVersionZip` tuple is proven through `Absent -> Staging -> Complete` with 16
  before/after crash injections, restart, retry, conflict, path, and disagreement controls.
- **Commit point:** the exact `Complete` SQLite transaction after verified final-file publication.
- **Production destination:** #965 resets the development-only Cache store to artifact-local publication and adds the
  managed filesystem effect seam. Recursive metadata and complete-root publication are deferred.
- **Packet SHA-256:** `58C854F457DC72AD4D6C10ABAABBD171BA6F421EC21439ABCAAB2B9BEDAE413B`.

## Proven-foundation candidates

GC-CAL-00 must verify these against exact branches and commits rather than assuming all are safely composable.

### Server-resolved materialization plans

Candidate behavior already exists for:

- immutable target-root resolution
- Direct, CachePreferred, and CacheRequired vocabulary
- target-root artifact descriptors
- cache-required-unavailable behavior
- client-facing source-shape constraints

Calibration use:

- reuse Direct and CacheRequired
- narrow the active cached artifact to `DirectoryVersionZip`
- leave CachePreferred inert or deferred rather than half-active

### Artifact grants and holder proofs

Candidate behavior already exists for:

- cache, root, artifact, requester, and holder binding
- canonical grant digest and P-256 signatures
- request-specific proof binding to method and normalized route
- validation-key publication and rollover rules
- generated-client canonicalization proof

Calibration use:

- preserve the existing approved validator and generated contract
- reject before local existence disclosure
- make no synchronous server validation call for a hit

GC-CAL-00 must detect current-source drift from the accepted #625 handoff and return an owner decision if two competing
contracts exist.

### Server cache registration and selection

Candidate behavior already exists for:

- server-created cache identity and registration
- repository assignment
- health, revocation, expiry, and selection
- server-side cache authorization seams

Calibration use:

- provision one current registration explicitly
- do not implement enrollment CLI, reconciliation, or liveness scheduling
- fail closed when the pre-provisioned registration is stale, revoked, or mismatched

### Grace.Cache project and local SQLite store

Candidate behavior exists on the ME3 branch for:

- Grace.Cache process and tests
- machine-scoped SQLite store
- pending and valid local state
- startup recovery and exclusive-store protection

Known design conflict:

- current store publication couples artifact validity to recursive metadata and a complete root set
- the calibration contract requires one `DirectoryVersionZip` artifact to become complete independently

GC-CAL-00 must select the minimal safe reshaping. Do not patch #624 around the conflict without a witness.

### Direct materialization

Direct is a proven regression boundary. Calibration work must not route Direct through cache registration, grant,
validation-key, read-through, or local cache state.

## Historical work that is not automatically reusable

Do not cherry-pick or merge these surfaces wholesale without the GC-CAL-00 salvage map:

- superseded enrollment and crash-recovery implementations
- automatic or candidate identity lifecycle
- bounded liveness scheduler code not required by calibration
- store APIs that require recursive metadata for every artifact commit
- issue-specific status ledgers and worker-control machinery
- open leaf branches produced under the previous review protocol
- review fixes whose surrounding architecture was later replaced

A merged child pull request is evidence of review and validation on its target branch. It is not proof that its behavior
belongs in the reset capability budget.

## GC-CAL-00 audit checklist

### Branch and commit salvage

- [ ] Fetch and prune all current refs.
- [ ] Record exact SHAs for `main`, top-level Epic #597 branch, ME3 branch, and any open cache pull-request heads.
- [ ] Compare unique commits with `git log --left-right --cherry-pick` and inspect merge ancestry.
- [ ] Map independently proven foundations to originating PRs and current files.
- [ ] Identify superseded lifecycle and process code to exclude.
- [ ] Select one exact production base or return an owner decision.
- [ ] Prove the selected base builds before new semantic work.

### Artifact algorithm witness

- [ ] Model `Absent -> Staging -> Complete` for one immutable target-root ZIP.
- [ ] Route every meaningful SQLite and filesystem effect through deterministic tracing.
- [ ] Inject failure after staging creation, byte write/close, digest validation, final-file publication, SQLite mutation,
  and terminal completion.
- [ ] Restart after every injected failure.
- [ ] Prove incomplete state is never a hit.
- [ ] Prove complete state survives restart.
- [ ] Prove same-tuple retry is safe.
- [ ] Prove conflicting metadata cannot replace valid content.
- [ ] Decide whether blob publication or SQLite completion is the commit point and how disagreement is handled.
- [ ] Record exact production seams and tests implied.

### Readiness output

- [ ] Update `docs/Grace Cache.md` only with accepted evidence.
- [ ] Record the exact branch base and commit salvage map.
- [ ] State whether GC-CAL-01 remains one coherent production issue.
- [ ] Return an owner decision when more than two enabling production changes are needed.

## GC-CAL-01 #965 implementation matrix

| Requirement | Current foundation | #965 work | Required proof |
| --- | --- | --- | --- |
| Artifact-local tuple | #623 recursive-metadata-coupled store | Reset development-only schema/API to exact kind, canonical identity, represented root, SHA-256, and size | Tuple validation and conflict cases |
| Artifact state | #623 pending/valid foundation | Replace with exact `Absent -> Staging -> Complete` transitions | State and valid-lookup tests |
| Managed filesystem | #624 design evidence only | Add opaque paths, same-root staging, streaming verification, and no-replace publication | Path, integrity, and publication-order cases |
| Restart classification | #623 acquire-before-recovery | Apply the finite GC-CAL-00 residue table | All 16 injected crash cases after store reopen |
| Retry and conflict | GC-CAL-00 evidence | Preserve exact content; allow same-tuple retry; reject conflicting tuples | Nine controls, including digest, size, root, kind, and identity conflicts |
| Complete disagreement | GC-CAL-00 evidence | Return recovery-required and preserve metadata for local reset | Missing/corrupt final-file proof |
| Process ownership | Merged #623 proof | Preserve without semantic expansion | Existing lock, WAL, foreign-key, busy, path, and operation-lifetime tests |
| Deferred surfaces | Existing server, CLI, and later Cache designs | Make no change | Static write-set and forbidden-capability scan |

## Deferred capability ledger

| Capability | Why absent now | Re-entry gate |
| --- | --- | --- |
| Recursive metadata and complete-root projection | It created the current store contract conflict before one artifact worked. | Separate Outcome Charter and witness after tracer. |
| CachePreferred | It adds fallback behavior beyond the forced cache path. | Tracer merged and Direct/CacheRequired measurements reviewed. |
| Enrollment CLI and status | Provisioning UX is not needed to prove artifact behavior. | Deployable operator increment selected. |
| Registration liveness | Scheduler and expiry transitions add another state machine. | Static process path proven and liveness value accepted. |
| WDU local materialization | Separate destructive working-tree transaction and #835 dependency. | #835 merged and cache artifact path proven. |
| Watch and Operations | Cross-epic contracts and additional authorities. | Separate owner decision after both product paths stabilize. |
| Prefetch and retention | Optional optimization and scheduler state. | Measured need after read-through operation. |
| HA/DR and platform parity | Hardened or broader deployment concerns. | Explicit Hardened or platform contract. |

## Review and merge audit

GC-CAL-01 #965 is merge-ready only when:

- [ ] the Factory Run Charter is frozen and unchanged
- [ ] the production diff implements no deferred capability
- [ ] all 25 witness-derived cases pass through focused production seams
- [ ] existing #623 connection and process-lock proof remains green
- [ ] no route, serving, network fill, grant, enrollment, liveness, recursive metadata, or complete-root behavior enters
      the slice
- [ ] current GitHub `Validate` passes or a concrete unrelated failure is documented
- [ ] R1 Discovery Review produced one finite frozen ledger
- [ ] when R1 accepted repairs, the issue-owner worker repaired them in one coherent pass
- [ ] when R1 accepted repairs, R2 Closure Review closed the ledger without a new major design surface
- [ ] public, durable, generated, and documentation surfaces are current
- [ ] residual risks match explicit deferrals

## Post-artifact-slice decision

Do not automatically resume the old mini-epic DAG. Measure #965, re-plan the broader miss-to-restart-hit tracer, and
choose exactly one next capability. The next specification update owns its own quality contract, capability budget,
readiness, witness, and tracer.
