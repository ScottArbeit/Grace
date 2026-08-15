# Grace Cache

**Status:** Algorithm-ready for the one-ZIP artifact tracer; broader Product V1 topology remains Design-ready
**Quality contract:** Product V1 for each selected production increment
**Canonical source:** `docs/Grace Cache.md`
**Evidence current through:** 2026-08-13 tracker and branch audit inputs

## 1. Product outcome

Grace Cache lets a Grace client retrieve server-resolved immutable materialization artifacts through a nearby
read-through cache without making the cache a second repository source of truth.

Grace Server owns repository meaning, user authorization, cache selection, repository assignment, registration state,
artifact-grant issuance, and read-through source authority. Grace Cache owns only local immutable artifact custody and
serving for requests that prove current permission.

The first post-calibration tracer supports one immutable `DirectoryVersionZip` artifact. It proves server-selected
identity, exact SHA-256 and size, artifact-local crash-safe commit, authorized CacheRequired miss/fill/serve/hit, and
unchanged Direct behavior. Recursive metadata and complete multi-artifact root publication remain Product V1 goals but
are not prerequisites for this first tracer. They require a later separately proven publication rule.

The accepted first production slice, GC-CAL-01 #965, is narrower than that tracer. It translates only the proven local
artifact lifecycle:

```text
Absent
  -> Staging
  -> exact size and SHA-256 verification
  -> no-replace final-file publication
  -> Complete
  -> process reopen
  -> verified local hit eligibility
```

GC-CAL-01 adds no public route, serving, grant validation, or server-side network fill. The broader tracer described in
sections 4 through 13 remains the Design-ready target for re-planning after the artifact slice runs. Direct
materialization remains unchanged.

## 2. Why the reset is necessary

The previous plan distributed the first real cache behavior across runtime, enrollment, liveness, SQLite, filesystem,
endpoint, source-authority, fill, diagnostics, local materialization, Operations, and Watch leaves. Review repeatedly
discovered state and authority questions after implementation began. The tracker produced many merged foundations but no
named miss-to-hit tracer.

The reset keeps independently proven foundations, removes optional capabilities from the first increment, requires an
executable artifact-commit witness, and implements the complete supported path in one vertical production slice.

## 3. Accepted calibration decisions

The owner accepted these decisions through the reviewed GC-CAL-00 reset:

1. The first cached artifact kind is `DirectoryVersionZip` only.
2. Recursive metadata, complete-root projection, and local working-directory materialization are deferred.
3. The first forced cache path uses `CacheRequired`; `CachePreferred` is deferred.
4. Direct remains supported and receives regression proof.
5. One cache identity and registration are provisioned explicitly for the supported environment. Enrollment CLI,
   automatic identity lifecycle, and registration-refresh scheduling are deferred.
6. One cache process serves one configured endpoint and one configured local store.
7. The implementation may serialize cache operations through one process-wide gate. Parallel fill throughput is not a
   calibration requirement.
8. The cache validates each artifact request locally from the signed grant, holder proof, and cached Grace Server
   validation keys. It makes no synchronous Grace Server callback merely to validate a hit request.
9. A miss obtains a new short-lived source only through a cache-side Grace Server operation that revalidates the current
   cache registration, repository assignment, grant, immutable root, and exact artifact.
10. No WDU, Watch, Grace.Operations, prefetch, retention scheduler, generalized retry queue, or generalized recovery
    protocol is part of the increment.

GC-CAL-00 also established these artifact-local decisions:

- Artifact-local `Complete` means exact final-file and SQLite agreement for one immutable artifact tuple.
- The local artifact commit point is the exact `Complete` SQLite transaction after verified final-file publication.
- Restart may complete `Staging + verified exact final`; all unknown or corrupt partial residue is cleaned or fails
  closed without promotion.
- `Complete` disagreement is recovery-required and uses explicit local reset; no automatic reconciliation is included.
- CachePreferred, recursive metadata, complete-root serving, enrollment, and liveness are outside the first artifact
  commit slice.

## 4. Supported actors, workflow, and environment

### Actors

- A Grace user or integration test requests a server-resolved Materialization Plan.
- Grace Server resolves one immutable `DirectoryVersionId`, selects one currently eligible pre-provisioned cache, and
  issues the artifact grant.
- One Grace Cache process validates the request, fills an absent target-root ZIP, and serves verified bytes.
- A test or operator provisions the cache identity, registration, repository assignment, endpoint, and protected local
  configuration before the workflow begins.

### Environment

- Linux x64 is the reference host profile.
- The process may be started manually or through Aspire for the calibration increment.
- Systemd packaging and installation are deferred.
- One cache process owns one machine-scoped SQLite database and managed artifact directory.
- The local cache directory and private key are protected by the selected operating-system account.
- HTTPS remains the intended deployment default. HTTP is allowed only in an explicitly configured local or Aspire test
  environment.

### Valuable data and threat model

- Cached artifacts are reproducible immutable copies. Corruption is unacceptable, but local eviction or full reset is
  an allowed recovery.
- User authorization and repository meaning remain server-owned.
- The threat model includes malformed or copied network requests and an untrusted ordinary client.
- Host compromise and hostile local administrators are out of scope.

## 5. Included, deferred, and rejected capabilities

| Capability | Calibration increment | Later Product V1 | Hardened or operations | Rejected |
| --- | --- | --- | --- | --- |
| Direct materialization | Regression proof | Yes | | |
| CacheRequired target-root ZIP | Yes | Yes | | |
| CachePreferred fallback | | Yes | | |
| Recursive metadata artifact | | Yes | | |
| Complete-root cache projection | | Yes | | |
| Per-request grant and holder-proof validation | Yes | Yes | | |
| Cache-side server-authorized miss source | Yes | Yes | | |
| Crash-safe immutable artifact store | Yes | Yes | | |
| Manual or Aspire provisioning | Yes | | | |
| Administrator enrollment CLI and status | | Candidate | | |
| Registration liveness scheduler | | Candidate | | |
| Local materialization through WDU | | Candidate after #835 | | |
| Prefetch and scheduled retention | | | Candidate | |
| Watch and Grace.Operations integration | | | Candidate | |
| HA/DR and broad platform parity | | | Candidate | |
| Automatic identity rotation | | | | Yes for Product V1 |
| Durable fill retry queue | | | | Yes unless a later contract explicitly selects it |

A later Product V1 increment must receive its own Outcome Charter, capability budget, readiness audit, and Algorithm
Readiness Witness when it adds Tier 2 behavior.

## 6. Domain language and identity

| Term | Meaning in this specification |
| --- | --- |
| Materialization Plan | Grace Server's response that resolves moving input to an immutable target root and a delivery mode. |
| Target-root ZIP | The `DirectoryVersionZip` artifact for one immutable target-root `DirectoryVersionId`. |
| Artifact identity | Closed artifact kind plus canonical artifact identifier, represented root, expected lowercase SHA-256, and expected byte size. |
| Artifact grant | Grace Server's short-lived signed permission binding requester, holder key, cache, target root, execution mode, and exact artifact. |
| Holder proof | Fresh request proof binding grant digest, method, normalized route, artifact identity, and proof time. |
| Read-through authority | A short-lived cache-side source returned by Grace Server after current registration, assignment, grant, root, and artifact revalidation. |
| Complete artifact | A final managed file whose bytes match expected size and SHA-256 and whose current SQLite state declares the same immutable identity complete. |
| Hit | A request admitted by current grant and holder proof for a complete artifact. Local presence alone is not a hit. |
| Miss | An admitted request for an artifact that is not complete locally. |

The immutable artifact tuple is:

```text
artifact kind
+ canonical artifact identity
+ represented DirectoryVersionId
+ expected lowercase SHA-256
+ expected byte size
```

A different value in any tuple member is a different artifact request or a conflict. Caller-controlled identity text is
data, never a local path.

## 7. Authority model

Grace Server is authoritative for:

- user and repository authorization
- selector resolution and immutable target root
- execution mode and cache selection
- cache identity, current registration, repository assignment, revocation, and expiry
- artifact-grant issuance
- read-through source authority

Grace Cache is authoritative only for:

- its configured local key and cache identity reference
- local artifact staging and complete state
- the managed path corresponding to an immutable artifact identity
- truthful local hit, miss, fill failure, and serve outcome

The cache never resolves branches, references, latest state, repository membership, or user access. The client never
receives the cache's read-through source authority in a `CacheRequired` plan or error.

## 8. State and effect model

The calibration increment has one new durable partial-state lifecycle:

```text
Absent -> Staging -> Complete
```

`Staging` is never serveable. `Complete` requires agreement between the final managed file and SQLite identity, digest,
and size state.

GC-CAL-00 selected this exact order:

1. allocate a same-root staging directory;
1. create one unique staging file;
1. write and close exact bytes;
1. verify exact byte size and lowercase SHA-256;
1. transactionally persist `Staging` with the complete tuple and operation identity;
1. atomically rename to the deterministic final path without replacement;
1. transactionally change the exact `Staging` row to `Complete`; and
1. publish terminal success only after final-file and SQLite agreement is rechecked.

The commit point is the exact `Complete` SQLite transaction after verified final-file publication. Restart applies this
finite residue rule:

- unknown staging without an SQLite tuple is deleted and remains `Absent`;
- staging-only residue or an exact `Staging` row without a final file cleans to `Absent`;
- exact `Staging` plus a verified final file completes through one exact recovery transaction;
- corrupt `Staging` residue cleans to `Absent` without promotion;
- exact `Complete` plus verified final bytes remains `Complete`;
- `Complete` disagreement is `RecoveryRequired` and preserves metadata for explicit local reset; and
- conflicting tuples preserve current content and fail closed.

The production implementation must follow the accepted evidence and preserve these invariants:

- authorization is checked before artifact existence is disclosed
- staging uses only a cache-derived managed path
- expected identity, size, and digest are fixed before bytes are accepted
- no partial or unverified bytes are a hit
- the commit point is explicit and crash-tested
- restart never upgrades unknown or partial state to complete without verification
- a retry of the same immutable tuple cannot replace a valid different artifact
- conflicting expected metadata fails closed
- complete state remains readable after process restart
- local reset or deletion is an allowed operator recovery

No durable retry queue, attempt history, backoff state, reconciliation worker, or scheduled cleanup state is included.
Request-driven cleanup of residue owned by the same immutable tuple is allowed when the witness proves it.

## 9. Supported workflow

### 9.1 Plan

1. A supported caller requests a `CacheRequired` Materialization Plan.
2. Grace Server resolves exactly one immutable target-root `DirectoryVersionId`.
3. Grace Server selects one current eligible pre-provisioned cache.
4. The client-facing plan names the selected cache endpoint, target-root ZIP descriptor, and artifact grant.
5. The client-facing plan exposes no Direct source.

### 9.2 Request admission

1. The client sends the exact artifact request to Grace Cache with the signed grant and fresh holder proof.
2. Grace Cache validates the grant signature and declared lifetime using its cached Grace Server validation-key set.
3. Grace Cache validates requester-holder binding, intended cache, target root, execution mode, exact artifact membership,
   normalized route, method, grant digest, and proof freshness.
4. Failure returns a stable sanitized rejection before local existence or timing-sensitive hit information is disclosed.

The existing approved grant and holder-proof contract remains authoritative unless GC-CAL-00 finds current source drift.
A contract conflict is an owner gate, not an invitation to invent a second validator.

### 9.3 Hit

1. After request admission, Grace Cache reads complete local state for the exact immutable tuple.
2. It verifies that the final managed file exists and matches the stored expected size and digest according to the
   accepted validation policy.
3. It streams the admitted response. Admission freshness is checked once before response bytes; an admitted response may
   stream to completion after grant or proof expiry.

Every retry, range request, parallel request, or additional artifact request requires a fresh proof and a grant valid at
admission.

### 9.4 Miss and fill

1. After admission finds no complete artifact, the cache enters the one process-wide operation gate.
2. It rechecks complete state because another serialized request may have filled the artifact.
3. On a persistent miss, the cache sends a cache-side read-through request to Grace Server.
4. Grace Server revalidates current cache identity and registration, repository assignment, grant, target root, and exact
   artifact membership.
5. Grace Server returns one short-lived exact source or a sanitized failure. It does not alter the client-facing plan.
6. Grace Cache stages bytes under its managed root, closes the stream, verifies exact byte size and lowercase SHA-256,
   and commits through the GC-CAL-00 algorithm.
7. Only after complete state is visible does the admitted request serve bytes.
8. A later separately admitted request returns a hit without another source request.

There is no automatic retry loop. A failed request returns a truthful error. A later client request may try again.

### 9.5 Restart

1. The process restarts against the same configured database and managed artifact directory.
2. It applies only the accepted request-driven or startup residue rule from GC-CAL-00.
3. It preserves valid complete artifacts.
4. A fresh admitted request for the completed tuple returns a hit.
5. A fresh admitted request for incomplete residue treats it as a miss or a stable fill failure, never a hit.

## 10. Functional requirements

### GC-CAL-REQ-001 - One immutable CacheRequired plan

- **Trigger:** a supported Materialization Plan request.
- **Result:** one immutable target-root `DirectoryVersionId`, one `DirectoryVersionZip` descriptor, one selected cache,
  and one bound artifact grant.
- **Failure:** no eligible cache returns the existing cache-required-unavailable behavior.
- **Invariant:** the client-facing plan contains no Direct source.
- **Forbidden:** recursive metadata, branch resolution inside the cache, or caller-selected cache authority.

### GC-CAL-REQ-002 - Pre-provisioned static cache identity

- **Trigger:** supported process startup.
- **Precondition:** local protected configuration and matching current server registration already exist.
- **Result:** the process can verify grants, identify itself for read-through authority, and listen on the configured
  endpoint.
- **Failure:** missing, corrupt, revoked, expired, or mismatched configuration fails closed.
- **Forbidden:** enrollment, rotation, reconciliation, promotion, or refresh scheduling in this increment.

### GC-CAL-REQ-003 - Admission before disclosure

- **Trigger:** artifact HTTP request.
- **Result:** valid local grant and holder proof admit the exact cache, root, route, method, and artifact.
- **Failure:** malformed, expired, wrong-cache, wrong-root, wrong-artifact, wrong-route, wrong-method, or wrong-holder
  requests fail before existence disclosure or response bytes.
- **Forbidden:** caller PAT or OIDC validation at Grace Cache, synchronous server validation of every hit, raw local paths.

### GC-CAL-REQ-004 - Artifact-local completeness

- **Trigger:** local hit check.
- **Result:** only one complete `DirectoryVersionZip` tuple is serveable.
- **Failure:** absent, staging, conflicting, missing-file, or corrupt state is not a hit.
- **Invariant:** artifact completeness is independent of recursive-metadata or full-root completeness in this increment.
- **Forbidden:** treating local file presence as valid state.

### GC-CAL-REQ-005 - Server-authorized read-through source

- **Trigger:** admitted miss.
- **Result:** the selected cache receives one short-lived source for the exact artifact.
- **Revalidation:** current cache registration, repository assignment, grant, target root, and artifact membership.
- **Failure:** stale, revoked, wrong-cache, wrong-repository, wrong-root, wrong-artifact, or malformed requests fail closed.
- **Invariant:** source details never enter the client-facing `CacheRequired` plan or sanitized client error.

### GC-CAL-REQ-006 - Verified crash-safe commit

- **Trigger:** successful source response.
- **Result:** exact bytes are staged, closed, size-checked, SHA-256-checked, and committed according to the accepted
  Algorithm Readiness Witness.
- **Failure:** transfer, source expiry, size mismatch, digest mismatch, storage error, database error, or injected crash
  creates no serveable state.
- **Restart:** complete state survives; incomplete residue follows the witnessed rule.
- **Forbidden:** serving before commit, replacing a conflicting complete artifact, or generalized retry state.

### GC-CAL-REQ-007 - Serve-after-commit and next-request hit

- **Trigger:** completed fill or complete local state.
- **Result:** the admitted request streams exact verified bytes; a later fresh admitted request is a hit.
- **Invariant:** every request is independently admitted.
- **Boundary:** ordinary concurrent requests serialize safely through the process-wide gate.

### GC-CAL-REQ-008 - Direct regression

- **Trigger:** supported Direct plan and materialization.
- **Result:** current Direct behavior and integrity proof remain unchanged.
- **Invariant:** cache validation, registration, and source-authority code are bypassed for Direct.

### GC-CAL-REQ-009 - Truthful sanitized failure

- **Trigger:** any supported failure.
- **Result:** callers receive stable non-secret error classification; structured logs retain correlation and safe evidence.
- **Invariant:** logs and errors do not expose private keys, grants, holder proofs, read-through sources, raw managed paths,
  or caller credentials.
- **Forbidden:** reporting a hit, ready, or successful fill before the relevant commit point.

## 11. Failure and outcome matrix

| Condition | Observable outcome | Durable result | Retry or recovery |
| --- | --- | --- | --- |
| No eligible cache | CacheRequired unavailable | None | Operator fixes registration or uses another supported mode later |
| Invalid request grant or proof | Sanitized rejection before disclosure | None | Caller obtains a fresh valid grant or proof |
| Admitted local hit | Exact bytes | Complete state unchanged | Fresh proof for every new request |
| Server rejects read-through | Sanitized fill failure | No new complete state | Later request may retry after authority changes |
| Transfer or source expiry | Sanitized fill failure | Non-serveable residue only | Later request follows witnessed cleanup/retry rule |
| Size or SHA-256 mismatch | Integrity failure | No complete state | New source/request required |
| Crash at any injected point | Process stops | Witness-defined residue | Restart never serves partial state |
| Restart with complete state | Fresh request is a hit | Complete state preserved | None |
| Restart with incomplete residue | Miss or stable failure | Never upgraded without verification | Request-driven witnessed rule |
| Conflicting immutable tuple | Stable conflict | Existing valid artifact preserved | Owner or development reset if contract drift caused it |

## 12. Contract propagation

GC-CAL-01 owns every surface required by the vertical tracer. Existing approved contracts should be reused rather than
copied.

| Surface | Calibration disposition |
| --- | --- |
| `MaterializationExecutionMode` | Reuse Direct and CacheRequired. Do not implement CachePreferred behavior here. |
| Materialization Plan DTOs | Reuse or narrow current target-root ZIP descriptor and cache source shape. |
| Artifact grant and holder proof | Reuse current canonical signed contract and generated-client behavior. |
| Cache validation-key publication | Reuse current cacheable validation-key contract. |
| Cache read-through request/response | Implement or reshape one cache-side exact-artifact operation. |
| Cache HTTP route | Implement one closed artifact route with no branch/reference/raw-path resolver. |
| Cache SQLite and filesystem state | Reshape only as required by the witnessed artifact-local lifecycle. |
| CLI | No new end-user cache mode or enrollment command in this increment. |
| SDK/generated clients | Update only for public server contracts that the tracer actually exposes. |
| Events | N/A unless current code requires a public event for this exact outcome. |
| Documentation | This specification, implementation audit, and concise run instructions. |
| Tests | Store witness-derived tests, cache endpoint tests, server authority tests, and end-to-end Aspire tracer. |

If current source contains a broader accepted public contract that cannot be left inert truthfully, GC-CAL-00 must return
an owner decision before implementation.

## 13. Proof strategy

### Algorithm Readiness Witness

GC-CAL-00 selected `origin/epic/601-grace-cache-runtime-store` at
`8852de4665372f438075fc6952410ea02902f8e6` and returned `PROVEN`. Its 16 injected crash cases and 9 controls prove:

- partial staging is never a hit
- commit point and SQLite/filesystem ordering are explicit
- restart preserves complete state
- restart never promotes unknown partial state
- same-tuple retry is safe
- conflicting tuple cannot replace valid content
- database and filesystem disagreement fails closed or is repaired only by the witnessed rule

### Focused production proof

GC-CAL-01 must include:

- valid grant and proof hit
- malformed, expired, wrong-cache, wrong-root, wrong-artifact, wrong-route, wrong-method, and wrong-holder rejection
- admission before existence disclosure
- authorized miss and exact source issuance
- source-authority negative cases
- transfer failure, source expiry, size mismatch, SHA-256 mismatch, database failure, and filesystem failure
- injected crash cases retained from the witness
- restart hit
- incomplete-residue restart behavior
- two ordinary concurrent requests serialized safely
- no branch, reference, latest, raw-path, or repository-wide cache route
- no read-through source in the client-facing CacheRequired plan or error
- unchanged Direct behavior

### End-to-end tracer

One Aspire-backed test must demonstrate:

```text
provision cache identity and registration
-> start Grace Server and Grace Cache
-> request CacheRequired plan
-> make first authorized artifact request
-> observe one source fill and exact bytes
-> restart Grace Cache
-> make second request with fresh proof
-> observe local hit and no source fill
```

The test should assert durable state and source-call count, not only HTTP status.

### Validation

Use focused project builds and tests during implementation. The merge candidate requires current GitHub `Validate` and
one R1 Discovery Review under the frozen Factory Run Charter, plus one targeted R2 Closure Review only when R1 produced accepted repairs.

## 14. Factory V2 implementation sequence

### GC-CAL-00 - Rebaseline and algorithm witness

Discovery work only. It selects the cleanest proven branch base and the artifact commit algorithm. It may use a temporary
branch or draft not-for-merge pull request. It does not merge production behavior.

Required outputs:

- exact branch and commit salvage map
- exact production base for GC-CAL-01
- `PROVEN`, `SIMPLIFIED`, or `BLOCKED` Algorithm Witness verdict and JSONL traces
- selected state/effect order and commit point
- required minimal store/API reshaping
- updated readiness verdict

### GC-CAL-01 #965 - One artifact-local commit slice

One Product V1 internal tracer issue and one coherent pull request. It resets the development-only Cache store/API to
the proven tuple lifecycle and adds the managed filesystem publication seam. It owns only six Cache production, test,
and project paths. It adds no route, serving, network fill, grant validation, enrollment, liveness, recursive metadata,
or complete-root publication.

### After the artifact slice

Run a factory retrospective and re-plan the broader miss-to-restart-hit tracer before choosing the next capability.
Candidate later increments are:

1. recursive metadata plus an explicit complete-root projection
2. CachePreferred fallback
3. administrator enrollment and redacted status
4. bounded registration liveness
5. local materialization through WDU after #835
6. diagnostics, then optional operational capabilities

Each is re-evaluated against user value and capability cost. This list is not an automatic backlog.

## 15. Tracker and branch disposition

- Keep Epic #597 as the product parent, but replace its active body with the Factory V2 reset contract.
- Keep completed GC-CAL-00 #964 linked as Discovery evidence and GC-CAL-01 #965 as the only active implementation
  child.
- Remove old active mini-epics from the parent relationship or mark them historical.
- Pause or close #601, #602, #624, #625, #626, #627, #857, and #958 as superseded or deferred evidence.
- Do not resume their branches or open pull requests automatically.
- Prompt 3 creates `epic/597-cache-calibration-v2` from a refreshed lineage that retains selected base
  `8852de4665372f438075fc6952410ea02902f8e6`, then creates `agent/965-one-zip-artifact-commit`.
- Do not choose a different branch or base by chronology alone.

Historical issues, comments, PRs, and branches remain evidence. They are not current authority when they conflict with
this specification.

## 16. Lifecycle verdict and owner gates

### Current verdict

- Product vision and broader miss-to-restart-hit tracer: Design-ready.
- One-ZIP artifact algorithm: `PROVEN` and Algorithm-ready.
- GC-CAL-01 #965: assignable under the frozen issue body, Factory Run Charter, selected base, and accepted handoff.

### Must interrupt the owner when

- the target-root ZIP cannot be cached independently of recursive metadata without changing desired product semantics
- current public contracts require CachePreferred, enrollment, or liveness to be active rather than deferred
- more than two enabling production pull requests are required before the tracer
- the clean branch base cannot be isolated without carrying superseded lifecycle machinery
- the witness cannot produce a crash-safe artifact-local commit without a new recovery protocol
- another authority, state machine, public lifecycle, or quality promise is needed
- a proposed simplification changes user-visible authorization, integrity, or Direct behavior

### Explicit residual risks

- Manual provisioning is not a deployable administrator experience.
- CachePreferred and recursive metadata are not proven.
- The calibration process-wide gate is not a throughput design.
- Systemd deployment, liveness, and local materialization are not included.
- Existing branch foundations require a fresh exact-commit salvage audit.

These are visible deferrals, not hidden incomplete behavior in the calibration increment.
