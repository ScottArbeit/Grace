# Grace Cache

**Status:** Plan-ready recovery topology
**Quality contract:** Product V1
**Canonical source:** `docs/Grace Cache.md`
**Evidence current through:** 2026-08-11, `epic/597-grace-cache-materialization-plans` at
`22ff0e18d327d0aaf0e963b0df737b81f47b36be`

## 1. Outcome

Grace Cache lets a Grace user materialize one server-resolved immutable directory root through Direct,
CachePreferred, or CacheRequired execution. Grace Server resolves repository meaning, applies access rules, selects
an eligible cache, and issues the short-lived artifact grant. Grace Cache serves only the full-root immutable artifacts
named in that plan. It does not resolve branches, evaluate user access, or become a second repository source of truth.

Product V1 uses one static P-256 service identity per cache installation. A Linux x64 cache service, running through
systemd under a system-managed account, keeps its private key and cache data in an OS-protected private directory. The
key is not exported through Grace. This is an operating-system storage promise, not a hardware-backed or
non-exportable-key promise.

The first cache tracer is complete when an eligible cache handles one valid full-root request as a miss, obtains the
exact source Grace Server selected, verifies its hash and size, atomically commits the artifacts, serves the bytes, and
then returns a hit for the same authorized artifact. Direct remains a supported path throughout the tracer.

## 2. Intent, scope, and non-goals

### Why this matters

Grace needs a reproducible materialization path for early users without turning an artifact cache into a repository
service. The cache path must preserve the server's plan, grant, integrity, and fallback decisions while reducing repeat
artifact retrieval.

### Supported actors, workflows, and environment

- A Grace user requests a server-resolved materialization plan and consumes the returned execution mode.
- A current Grace administrator enrolls, revokes, or manually re-enrolls one cache in an Owner or Organization
  boundary with explicit repository assignments.
- A Linux x64 host runs one systemd-managed cache process under its configured system-managed service account.
- Grace Server is the source of truth for plan resolution, repository assignment, cache eligibility, grant issuance,
  liveness, revocation, and expiration.
- Grace Cache is an artifact-serving process only. Its durable local state is static installation configuration,
  private-key reference, artifact store state, and truthful local status.

### Required now

- Server-resolved immutable Materialization Plans for target-root zip and recursive metadata artifacts.
- Direct, CachePreferred, and CacheRequired execution with their distinct fallback behavior.
- One static cache service identity, administrator enrollment, manual revoke/delete/re-enrollment recovery, and
  redacted status.
- One bounded registration-refresh scheduler driven by server-issued liveness times.
- Validated local SQLite/filesystem artifact storage, authorized artifact serving, and server-authorized read-through
  fill.
- Artifact-grant validation-key rollover where the existing grant-verification contract advertises it.
- Local materialization through the Working Directory Update path after Epic #835 has merged to `main`.

### Deferred, rejected, and out of scope

- Automatic or startup cache service-identity rotation, active/candidate promotion, `rotate-now`, rotation timers,
  rotation retries, and rotation reconciliation are deferred from Product V1.
- SignalR prefetch and scheduled retention or eviction are deferred. Correctness cleanup for incomplete staging or
  incomplete commits remains required.
- Watch integration, Grace.Operations integration, broad platform parity, high availability, disaster recovery, and
  hostile-root defense are out of scope.
- Windows and macOS are not advertised cache-host platforms for Product V1.
- Automated enrollment reconciliation, a durable retry queue, daemon installation, hardware-backed custody, and a
  Grace private-key export feature are not included.
- Compatibility or migration machinery for unmerged, deleteable development state is rejected.

## 3. Current-state evidence

| Evidence | Current state at the live reference | Specification use |
| --- | --- | --- |
| Owner decision, [issue comment](https://github.com/ScottArbeit/Grace/issues/597#issuecomment-5263063211) | Product V1 governs; static identity replaces automatic rotation; R0, R1, and R2 replace #622. | Accepted product and scope decisions. |
| [Epic #597](https://github.com/ScottArbeit/Grace/issues/597) | The approved Product V1 decision is the governing cache contract. Older rotation text in the issue is not current contract text. | R0 removes stale public and internal rotation surfaces. |
| [Mini-epic #601](https://github.com/ScottArbeit/Grace/issues/601) | Open parent for the replacement runtime and store work. | Hosts the R0, R1, and R2 replacement leaves. |
| [PR #723](https://github.com/ScottArbeit/Grace/pull/723) | Closed as superseded without merge at `647f4067252e5f2805e76a492d26096a854a75a9`. | Selective salvage source only; its behavioral commits are not reused wholesale. |
| [Issue #622](https://github.com/ScottArbeit/Grace/issues/622) and [issue #724](https://github.com/ScottArbeit/Grace/issues/724) | Closed and not planned for Product V1. | Neither is a future implementation container. |
| [Issue #855](https://github.com/ScottArbeit/Grace/issues/855) | R0 static-contract pruning is complete. | Static cache identity is the current source contract. |
| [Issue #856](https://github.com/ScottArbeit/Grace/issues/856) | Superseded; the current R1 tracker record replaces its mixed scope. | Do not resume it. |
| [Issue #857](https://github.com/ScottArbeit/Grace/issues/857) | R2 registration liveness remains planned. | It follows the R1 sequence. |
| [Issue #835](https://github.com/ScottArbeit/Grace/issues/835) | Open. `WorkingDirectoryUpdate.run` is not available to this epic until #835 merges to `main` and Epic #597 is refreshed. | Hard sequencing gate for #628 to #630 and #634. |
| `src/Grace.Types/MaterializationExecutionMode.Types.fs` (`MaterializationExecutionMode`) | Defines `Direct`, `CachePreferred`, and `CacheRequired`. | Existing public execution-mode contract. |
| `src/Grace.Types/MaterializationPlan.Types.fs` (`MaterializationPlan.CacheRequiredAvailability.ErrorCode`) and `src/Grace.Server/Materialization.Server.fs` | Define server-resolved target-root artifacts, cache selection, and `cacheRequiredUnavailable`. | Existing plan and failure seam. |
| `src/Grace.Actors/CacheRegistration.Actor.fs` (`CacheRegistrationActor`), `src/Grace.Server/CacheRegistration.Server.fs`, and related shared/types files | Current server registration supports static enrollment, refresh, revocation, and repository assignment. Cache service-identity rotation is removed. | Preserve the separate artifact-grant validation-key rollover behavior. |
| `src/OpenAPI/Cache.Components.OpenAPI.yaml` and `src/OpenAPI/Grace.OpenAPI.yaml` | Current generated input has no `RotationDueAt`, cache key-rotation request, or `/cache/rotate-key` surface; `/cache/validation-keys` remains separate. | R0 completion and distinct-key rule. |
| `src/Grace.Server.Security/ArtifactGrantKeys.Server.fs` (`ArtifactGrantKeyRing`) and `src/Grace.Actors/ArtifactGrantSigningKey.Actor.fs` | Grace Server publishes a validation-key set for artifact-grant verification. | Existing artifact-grant validation-key rollover remains in scope. |
| `src/Grace.Server.Unit.Tests/MaterializationPlan.Server.Tests.fs`, `src/Grace.Types.Tests/MaterializationPlan.Types.Tests.fs`, and `src/Grace.Types.Tests/CacheRegistration.Types.Tests.fs` | Focused seams exist for current plan and registration contracts. | Starting proof seams; later work adds Product V1 cases. |
| `docs/Grace Cache implementation audit.md` | Product V1 audit records completed server foundations, planned cache-host work, the tracer, and release gates. | Audit companion; it does not replace this specification or the issue tracker. |
| `C:\Source\Grace\skills\grace\references\specification-profile.md` | Grace specification profile requires explicit surfaces, identity freshness, ordering, and proof. | Current specification profile input; read only, not copied into this worktree. |

The specification profile is supplied from `C:\Source\Grace` for this recovery work. This specification applies that
profile with the repository instructions, Product V1 quality contract, portable specification contract, current source
evidence, and the durable owner decision.

### Current R1 tracker record

| Tracker | Current classification | Scope and sequence |
| --- | --- | --- |
| #886 | Implemented and proven; completed evidence. | R1A static enrollment identity is complete. |
| PR #888 | Merged implementation and proof for #886. | Records the completed R1A implementation and focused proof. |
| #887 | Superseded mixed enrollment/status issue. | It does not own current delivery work. |
| PR #896 | Closed superseded mixed enrollment/status implementation. | Evidence only; do not reuse it wholesale. |
| #904 | Superseded status issue. | It does not own current delivery work. |
| PR #907 | Closed superseded status implementation. | Evidence only; do not reuse it wholesale. |
| #913 / #914 | Current docs-only correction; planned pure local status follows. | #913 reconciles this record; #914 then owns status only. |
| #905 | Planned one-shot enrollment after #913 and #914. | It owns enrollment only after the docs and status leaves complete. |

### Enrollment ambiguity disposition

An inactive accepted server enrollment is unselectable. Fresh manual enrollment is allowed. Server expiry performs
eventual cleanup. Grace Cache adds no automatic enrollment retry or reconciliation.

## 4. Quality contract and accepted risk

This work uses the `Product V1` profile in
`C:\Users\scott\.codex\skills\engineering\dev-process\QUALITY-CONTRACTS.md`.

The release candidate must correctly preserve advertised workflows, access rules, immutable-artifact integrity,
durability selected by the cache, truthful liveness and status, public and generated contract propagation, and restart
or ordering behavior created by read-through and the registration scheduler.

The accepted deployment and risk boundary is deliberately narrow:

- One selected host profile: Linux x64, systemd, a system-managed account, and an OS-protected private cache/key
  directory.
- One static cache service identity lasts until a current administrator revokes it and the host is manually
  re-enrolled.
- A missed or ambiguous enrollment result does not start automatic retry or reconciliation. An inactive accepted server
  enrollment is unselectable, fresh manual enrollment is allowed, and server expiry performs eventual cleanup.
- Product V1 does not promise a platform-neutral keystore, high availability, disaster recovery, a defense against a
  hostile local root user, or automatic recovery from every local corruption event.

Review must reject a false success in a supported path. It must not add deferred rotation, prefetch, reconciliation,
or unsupported-platform work merely because those capabilities could be useful later.

## 5. Decisions and capability inventory

### Decision ledger

| ID | Decision | Lane | Status | Product V1 rule | Owner / proof impact |
| --- | --- | --- | --- | --- | --- |
| DEC-GC-001 | Quality level | product | accepted | Product V1 governs every leaf and final release candidate. | Owner decision; each leaf declares the profile and proof. |
| DEC-GC-002 | Cache service identity | domain | accepted | One static P-256 identity; no automatic or startup rotation. | R0 removes old rotation contract; R1 creates one static identity. |
| DEC-GC-003 | Key custody promise | scope | accepted | OS-protected private storage and no Grace export surface; no hardware-backed claim. | R1 host and redaction proof. |
| DEC-GC-004 | Enrollment ambiguity | product | accepted | An inactive accepted enrollment is unselectable; fresh manual enrollment is allowed; server expiry performs eventual cleanup; no automatic retry or reconciliation is added. | #886 completed the supporting implementation and proof; #905 consumes the disposition without changing it. |
| DEC-GC-005 | Registration liveness | architecture | accepted | One scheduler, server-issued liveness times, bounded retry, and fail-closed expiration or definitive rejection. | R2 transition and fake-clock proof. |
| DEC-GC-006 | Artifact-grant keys | domain | accepted | Keep existing artifact-grant validation-key rollover distinct from cache service identity. | R0 inventory must not remove grant-key behavior. |
| DEC-GC-007 | Local mutation | architecture | accepted | Later materialization prepares exact content and calls `WorkingDirectoryUpdate.run`. | #835 merge and epic refresh gate before affected leaves. |
| DEC-GC-008 | Recovery posture | scope | accepted | Manual reset, revocation, and re-enrollment replace automated identity or enrollment repair. | Runbook and status proof. |
| DEC-GC-009 | Deferred capabilities | scope | accepted | Prefetch, scheduled retention, Watch, Operations, platform parity, HA/DR, and hostile-root defense stay absent. | No half-active public surface or timer. |
| DEC-GC-010 | Final merge | workflow | accepted | The final epic-to-`main` merge needs Scott's explicit approval on the reviewed and validated current head. | Release gate. |

### Capability inventory

| Capability | Disposition | Outcome and environment | Intrinsic obligations and proof | Reason |
| --- | --- | --- | --- | --- |
| Server-resolved full-root plans | required now | Grace user on all supported materialization paths | Resolved immutable root, complete artifacts, mode-specific plan, contract tests | Core user value and source boundary. |
| Static enrollment and status | required now | Administrator on selected Linux host | Atomic local configuration, key matching, redaction, rejected-path proof | One usable cache identity without rotation machinery. |
| Registration liveness | required now | One running cache process | Server time, scheduler, expiry, shutdown, status, fake-clock proof | Eligibility must not outlive registration. |
| Artifact store and read-through | required now | Eligible cache serving full-root plan artifacts | Hash/size validation, atomic commit, completeness, per-request grant proof | Cache hit is useful only after correct miss fill. |
| Direct, CachePreferred, CacheRequired | required now | Grace user | Exact fallback or fail-closed behavior and generated-contract proof | Existing execution modes are public behavior. |
| Artifact-grant validation-key rollover | required now | Cache verifies server grants | Current/overlap validation and unknown-key fail-closed proof | Already advertised grant-verification contract. |
| Automatic cache service-identity rotation | deferred | None | Would add promotion, timers, retries, and recovery state | Product V1 removes the capability rather than weakening its correctness. |
| Prefetch and scheduled retention | deferred | None | Would add background scheduling and retention decisions | Read-through is the correctness path. |
| Watch and Grace.Operations integration | deferred | None | Cross-epic contract and stale-state proof | Their dependency contracts are not ready. |
| Platform parity, HA/DR, hostile-root defense | out of scope | None | Broader platform and recovery guarantees | Outside the approved V1 deployment profile. |

## 6. Domain language and model

| Term | Meaning in this specification |
| --- | --- |
| Materialization Plan | Grace Server's response that resolves moving input to one immutable target root and its required artifacts. |
| Full-root artifact | The target-root zip or recursive metadata artifact named by the plan. Whole-file, manifest, and content-block artifacts are not V1 cache artifacts. |
| Cache service identity | The cache's static P-256 public key plus server-assigned `CacheId`. It proves a cache's registration operations and is unrelated to a user's artifact request proof. |
| Artifact grant | Grace Server's short-lived, signed permission for one requester, holder key, cache, target root, execution mode, and full-root artifact set. |
| Artifact-grant validation key | A public Grace Server signing key used to verify artifact grants. Its rollover is separate from cache service identity. |
| Eligible cache | A cache selected by Grace Server because its registration is unrevoked, unexpired, healthy, and assigned to the resolved repository boundary. |
| Ready configuration | Atomically committed local machine configuration that references one locally usable key and a server-accepted identity. |
| Inactive accepted enrollment | A possible server enrollment left after an ambiguous request result before ready configuration commits. It is unselectable, does not prevent fresh manual enrollment, and expires under the server rule. |
| Complete artifact | Locally staged and validated bytes plus committed metadata for every full-root artifact required by one plan. Local presence alone is neither user permission nor cache eligibility. |

The identity tuple for one plan is the requester, repository, selector kind and selector value, resolved target-root
`DirectoryVersionId`, execution mode, required artifact identities, and selected cache when mode is not Direct. The
identity tuple for a cache registration is `CacheId`, static canonical public key, Owner or Organization boundary,
explicit repository assignments, exact endpoint, health, and current server registration lifetime. The identity tuple
for a cache artifact request is the signed-grant key id and digest, requester, holder-key thumbprint, selected cache,
target root, execution mode, artifact identity, normalized route, method, and proof time.

A cache is stale when its server lifetime has elapsed or it is revoked; it is conflicting when any immutable enrolled
fact differs. An artifact request is stale when its grant, validation key, or holder proof is expired, mismatched, or
invalid. The cache must re-read the current server result before publishing availability, verify the grant and request
proof before every serve, and validate exact prepared content before making later local materialization visible.

## 7. Supported scenarios and workflows

### Static enrollment and status

1. An administrator invokes enrollment on the selected Linux host.
2. The command validates host profile, endpoint, directory permissions, input boundary, and output destination before
   any server call.
3. The cache generates one P-256 key in a fixed protected staging location and sends only the canonical public key.
4. On success, it atomically persists ready configuration that refers to that key. `status` then reports enrolled with
   redacted identifiers and state.
5. On definitive rejection, it deletes the staged key and remains not enrolled.
6. On an ambiguous response or a crash before ready configuration commits, the next command removes an unreferenced
   staged key and reports not enrolled. It does not retry or reconcile automatically.
7. An inactive accepted server enrollment remains unselectable, allows fresh manual enrollment, and expires under the
   server rule. This path does not add automatic retry or reconciliation.
8. Missing, corrupt, or mismatched configured key material fails closed. The recovery is server revocation, local reset,
   and manual re-enrollment.

Completed R1A work (#886 and PR #888) established the static local identity boundary. It creates
`<root>/attempt/identity.pk8` at `0600` beneath a `0700` root and attempt directory, and publishes a `0700`
`<root>/ready` directory only after writing a matching `0600` `registration.json`. The fingerprint is the base64url
SHA-256 of the derived P-256 `X || Y` bytes. Inspection is read-only and returns only missing, attempt-present, ready,
invalid, or inaccessible; unsupported platforms fail before local mutation. The completed work added no HTTP request,
credential acquisition, listener, status output, retry, reconciliation, key rotation, or artifact serving.

The administrator enrollment request has no caller `Health`. Grace Server durably creates every registration as
`Unhealthy`, so existing eligibility selection rejects it until R2's authenticated refresh publishes a healthy state.
The actor saves that durable transition before returning enrollment success.

### Run and bounded registration liveness

1. `grace cache run` acquires its machine-wide guard before configuration, key, listener, store, or server work.
2. It validates ready configuration and the static key, then starts only the exact enrolled endpoint in a not-ready
   state.
3. It makes signed startup registration or refresh. Failure stops the listener and exits nonzero without publishing
   ready.
4. A successful server result publishes current status and schedules exactly one future callback from server-issued
   `RefreshAfter` and `ActiveUntil` values.
5. A definitive access or revocation result stops serving, publishes terminal failure, and exits nonzero.
6. A temporary or unknown refresh result is usable only before `ActiveUntil`. It retries at the R2-documented bounded
   interval, honors a safe server `Retry-After` only when it is capped by expiry, and never creates another scheduler.
7. At `ActiveUntil`, serving stops, status becomes expired, and the process exits nonzero. Restart has no durable retry
   or recovery queue; it repeats startup registration.

### Plan modes and artifact delivery

- **Direct:** Grace Server returns direct full-root sources. The client retrieves, validates, stages, and materializes
  them without cache use.
- **CachePreferred:** Grace Server returns a cache plan only when a current eligible cache is selected. Ordinary cache
  absence or accepted cache unavailability uses Direct fallback. A cache hit or miss still requires the returned grant.
- **CacheRequired:** Grace Server does not reveal a Direct source. Missing eligibility, cache failure, malformed grant,
  or cache miss that cannot complete fails closed with the existing cache-required error behavior rather than using
  Direct fallback.
- **Read-through miss:** Grace Cache verifies the plan/grant/request proof before serving. For a miss it fetches only
  the exact server-selected full-root artifacts, validates hash and size, commits a complete set atomically, then
  serves. Partial or unverified staging is never a hit.
- **Read-through hit:** Grace Cache again validates the current request grant and holder proof, then serves only a
  complete local artifact. A prior hit never replaces per-request validation.

### Later local materialization

Only after #835 merges to `main` and this epic is refreshed from that `main` head, CachePreferred and CacheRequired
local materialization prepares exact validated content and calls `WorkingDirectoryUpdate.run`. Cache does not retain or
create another local working-tree mutation, SQLite-completion, Branch/Watch-finalization, or recovery path.

## 8. Functional requirements and invariants

### GC-REQ-001 - Server-resolved immutable plan

- **Trigger:** a supported materialization request.
- **Preconditions:** Grace Server resolves the target and applies current user and repository checks.
- **Result:** the response names one immutable `DirectoryVersionId`, target-root zip, recursive metadata, and the
  selected mode/source shape.
- **Failure:** invalid or unsupported inputs fail before partial plan publication.
- **Invariant:** cache code never resolves branches, references, or user access itself.
- **Forbidden shortcut:** accepting a client-provided cache scope or deriving target content from local cache state.

### GC-REQ-002 - Static cache service identity

- **Trigger:** administrator enrollment or manual re-enrollment.
- **Preconditions:** selected Linux host profile, protected storage location, valid administrator request, and one
  canonical P-256 public key.
- **Result:** one server-accepted `CacheId` and a ready configuration tied to the same locally usable key.
- **Durable result:** private key stays local; ready configuration is atomic and contains no secret output surface.
- **Failure:** definitive rejection removes staged key; missing or mismatched key fails closed.
- **Invariant:** V1 exposes no automatic or startup identity rotation contract.
- **Forbidden shortcut:** reporting enrolled before both server acceptance and local ready-config commit.

### GC-REQ-003 - Resolved enrollment ambiguity disposition

- **Trigger:** an accepted server enrollment remains after an ambiguous response or a crash before ready configuration
  commits.
- **Result:** the inactive enrollment is unselectable, fresh manual enrollment is allowed, and server expiry performs
  eventual cleanup.
- **Invariant:** Grace Cache does not add automatic enrollment retry, reconciliation, background lookup, or a durable
  recovery workflow.
- **Evidence:** #886 and PR #888 completed the static enrollment implementation and focused proof. #905 consumes this
  disposition for one-shot enrollment.

### GC-REQ-004 - One bounded liveness scheduler

- **Trigger:** successful startup registration or refresh.
- **Preconditions:** valid ready configuration, static key, exact listener endpoint, and a server result with current
  liveness times.
- **Result:** one scheduler refreshes before expiration and keeps status aligned with the same server lifetime.
- **Failure:** definitive access/revocation failure and expiration stop serving and exit nonzero; temporary failure is
  bounded by expiry.
- **Invariant:** neither the cache nor Grace Server reports the cache available beyond its server-issued active time.
- **Forbidden shortcut:** a second timer, a durable retry queue, or treating a local clock as a reason to extend life.

When a terminal liveness result and best-effort local cleanup can both occur, the terminal state is durably recorded and
published before cleanup. Cleanup failure may retain diagnostic material, but it must not leave the cache displayed as
ready or registered.

### GC-REQ-005 - Eligible-cache selection

- **Trigger:** CachePreferred or CacheRequired plan resolution.
- **Preconditions:** a registration is healthy, unrevoked, current, and explicitly assigned to the resolved repository.
- **Result:** only the selected cache receives a cache-mode plan and grant.
- **Failure:** CachePreferred follows its explicit Direct fallback rule; CacheRequired fails closed.
- **Invariant:** name-like, wildcard, storage-pool-only, deleted, stale, or unrelated repository facts never select a
  cache.
- **Forbidden shortcut:** using a cache endpoint or local artifact as evidence of current eligibility.

### GC-REQ-006 - Authorized complete read-through

- **Trigger:** a cache-mode artifact request.
- **Preconditions:** a current grant, holder proof, exact route/method/artifact binding, and full-root artifact shape.
- **Result:** an authorized complete hit is served; a miss obtains and validates exactly the server-selected bytes before
  atomically becoming a hit.
- **Durable result:** committed metadata describes only a complete verified artifact set.
- **Failure:** grant, proof, source, hash, size, staging, or commit failure serves no partial data and leaves no hit.
- **Invariant:** local bytes are never treated as user permission or complete content without validation.
- **Forbidden shortcut:** write-through before hash and size validation, or serving a partially committed set.

### GC-REQ-007 - Direct, CachePreferred, and CacheRequired behavior

- **Trigger:** caller selects one public execution mode.
- **Result:** Direct bypasses cache; CachePreferred uses only the specified fallback; CacheRequired never receives a
  Direct source or silently falls back.
- **Failure:** CacheRequired returns its defined unavailable result when the cache path cannot serve the plan.
- **Invariant:** mode behavior is identical across DTO, API, SDK, CLI, documentation, and execution.
- **Forbidden shortcut:** treating all cache failures as a Direct fallback.

### GC-REQ-008 - Working Directory Update sequence

- **Trigger:** later cache-assisted local materialization after its dependency gate.
- **Preconditions:** #835 is merged to `main`; this epic is refreshed; exact content is validated and prepared.
- **Result:** the materialization invokes `WorkingDirectoryUpdate.run` for local publication and completion.
- **Failure:** no alternate local completion path becomes visible.
- **Invariant:** Cache owns retrieval and fallback policy, not local working-tree mutation semantics.
- **Forbidden shortcut:** recreating working-tree, SQLite, Branch, Watch, or Doctor completion logic in cache code.

### GC-REQ-009 - Truthful status and diagnostics

- **Trigger:** `status`, `run`, enrollment, refresh, expiry, or terminal failure.
- **Result:** human and JSON output distinguish not enrolled, enrolled-not-running, starting, ready, retrying before
  expiry, expired, revoked, and local-recovery-required states as each is included by its leaf.
- **Failure:** unavailable proof key, terminal server result, or expiry never displays ready.
- **Invariant:** status comes from current protected local state plus current liveness result, never a stale startup
  snapshot.
- **Forbidden shortcut:** reporting healthy because a listener started or because old configuration exists.

### GC-REQ-010 - Security, redaction, and reset

- **Trigger:** any API, CLI, log, diagnostic, local file, or failure path.
- **Result:** private keys, raw secrets, grant bodies, and opaque private references are absent from Grace outputs.
- **Failure:** unreadable or mismatched local custody fails closed with operator-safe recovery instructions.
- **Invariant:** reset means revoke/delete/re-enroll; no backward-compatibility layer preserves development state.
- **Forbidden shortcut:** exporting a private key to make recovery easier.

### GC-REQ-011 - Contract pruning

- **Trigger:** R0 static-contract inventory.
- **Result:** every public, persisted, generated, executable, and documented cache service-identity surface advertises
  static identity only or is removed.
- **Failure:** a discovered rotation or candidate surface expands R0's inventory and blocks completion until given a
  disposition.
- **Invariant:** artifact-grant validation-key rollover stays intact when identity-rotation surfaces are removed.
- **Forbidden shortcut:** deleting grant-key publication because it also uses P-256 keys.

## 9. Interfaces, contracts, and propagation

| Surface | Current or intended Product V1 rule | Disposition | Proof |
| --- | --- | --- | --- |
| Shared types and DTOs | Execution modes remain; cache service identity becomes static; rotation/candidate values are absent; grant-key publication remains. | R0 update | Type serialization and negative stale-value scans. |
| Orleans registration actor and state | Server registration stores immutable enrollment facts and liveness only; no service-identity promotion state. | R0/R2 update | Actor lifecycle, state persistence, selection, and expiry tests. |
| Server routes | Administrator enrollment/revoke and signed registration refresh remain as rebaselined; `/cache/rotate-key` is removed; grant-key route remains. | R0/R1/R2 update | Route access, validation, and OpenAPI tests. |
| Cache host and CLI | `grace cache enroll`, `status`, and `run` are added in their separate leaves with stable human/JSON output. | R1/R2 new | Command inventory, redaction, host-profile, and process tests. |
| Local configuration and key files | One protected staging key and one atomic ready configuration; no secrets in output. | R1 new | Crash, cleanup, atomic-write, mismatch, and permission proof. |
| Local SQLite/filesystem store | Metadata records complete verified full-root artifacts and correctness cleanup only. | Runtime/store leaves | Atomicity, partial-failure, restart, and integrity proof. |
| Materialization API and OpenAPI | Modes and cache-plan/grant shapes stay truthful; CacheRequired has no Direct source. | Existing plus later leaves | OpenAPI generation, freshness, generated SDK matrix, and response tests. |
| SDK and CLI consumers | Callers preserve mode semantics and use only the returned plan sources and grants. | #628 to #630 after #835 gate | End-to-end mode and negative fallback proof. |
| Working Directory Update | Cache-assisted local publication calls `WorkingDirectoryUpdate.run` only after #835 merge and refresh. | Deferred to named sequence | Cross-epic integration proof. |
| Documentation and implementation audit | This file is the current contract; existing audit records implementation classification but is not rewritten here. | This change plus R0 | MarkdownLint and final audit traceability. |

No migration is needed. Grace has no production data, and unmerged development cache state may be deleted and recreated
to establish the static Product V1 shape.

### Additional Grace surface dispositions

| Surface group | Disposition | Reason and required evidence |
| --- | --- | --- |
| `Grace.Shared` commands, validators, envelopes, hashing, and shared helpers | R0/R1/R2 update when their public contract changes | Static identity and liveness must validate and serialize consistently with types and routes. |
| Durable actor state and events | R0/R2 update | Remove service-identity rotation state; keep only states required by static registration and liveness. Restart/replay proof follows included durable state. |
| Registries, manifests, routing tables, and executable metadata | R0 inventory and update or unchanged with reason | Every rotation or candidate entry receives an explicit disposition; no inactive configuration remains. |
| Giraffe handlers, access rules, status codes, and error envelopes | R0/R1/R2 update | Enrollment remains administrator-gated; signed cache refresh and artifact-grant publication retain their intended access behavior. |
| CLI commands, flags, defaults, help, human output, and JSON output | R1/R2 update | `enroll`, `status`, and `run` need stable redacted output; unsupported host profile is rejected or not advertised. |
| SDK facades and generated clients | R0 and later mode leaves update | Static OpenAPI projection and materialization failure results must match generated clients. |
| Webhook, approval, SignalR, Watch, search, and projection behavior | Deferred or not applicable with reason | Prefetch, Watch, and Operations work are deferred; no new webhook, approval, search, or projection feature is selected. |
| SQLite, filesystem, and cache state | Runtime/store leaves update | Complete-set commit, integrity, cleanup, restart, and redaction behavior are required. |
| Hosted services, Aspire, Docker, runtime settings, and deployment scripts | R1/R2 or runtime leaves update | The selected Linux systemd profile must be explicit; no Windows/macOS promise or rotation setting remains. |
| Observability, structured logging, health, diagnostics, and operator guidance | R1/R2 update | Published status follows protected local facts and server liveness; output redacts private material. |
| Unit, actor, server integration, CLI, SDK, generated-contract, and Aspire tests | Updated by each owning leaf | Proof matches public behavior and catches false cache success. |
| README, contributor docs, command examples, architecture docs, and relevant agent guidance | R0 and later docs leaves update when affected | Current user-facing material follows this canonical contract; this initial specification intentionally leaves current-surface pruning to R0. |

## 10. Identity, state, time, and outcomes

### Identity and revalidation model

| Concern | Contract |
| --- | --- |
| Plan source of truth | Grace Server resolves moving repository input to one immutable `DirectoryVersionId` before cache interaction. |
| Cache eligibility source of truth | Current server registration, explicit repository assignment, liveness, and revocation state at plan selection. |
| Artifact permission source of truth | Current signed grant and per-request holder proof for the exact selected cache, root, artifact, route, and method. |
| Cache service-identity source of truth | Server-assigned `CacheId` and one canonical static public key, matched to the protected local private key. |
| Revalidation | Verify grant/proof on every artifact request; use server liveness results for run status; re-read required local configuration before publishing state. |
| Stale or conflicting facts | Expired/revoked registration, mismatched static key, changed endpoint, incomplete artifact, expired grant, or invalid holder proof fails closed. |

### Cache host state and time model

| State | Entry condition | Allowed exit | Time or restart rule | Published status |
| --- | --- | --- | --- | --- |
| Not enrolled | No ready configuration | Enrollment begins | No timer | Not enrolled |
| Staging enrollment | Protected staging key exists before server effect | Ready configuration, cleanup, or crash | On next command, remove unreferenced staging key | Not enrolled or in progress |
| Enrolled not running | Ready configuration and matching key exist | Run begins, reset, or local failure | No scheduler | Enrolled |
| Starting | Guard held, configuration/key validated, listener not ready | Registered current or stopped | Startup must not publish ready first | Starting |
| Ready | Server registration current and listener serving exact endpoint | Refreshing, terminal failure, or expired | One scheduler based on server values | Ready |
| Refreshing | One scheduled callback is executing | Ready, bounded retry, terminal failure, or expired | No overlapping scheduler | Refreshing or retrying |
| Retry before expiry | Temporary/unknown refresh failure before `ActiveUntil` | Ready, terminal failure, or expired | One capped retry schedule | Retrying |
| Expired | `ActiveUntil` reached | Process exit or fresh startup | Serving stops at boundary | Expired |
| Terminal failure | Definitive access/revocation result or unusable protected key | Manual reset and re-enrollment | No automatic work | Recovery required |

### Failure and outcome matrix

| Outcome | Durable truth and cleanup | User or operator result | Required proof |
| --- | --- | --- | --- |
| Enrollment validation fails | No server effect; no ready configuration | Clear rejected input; no enrolled status | Validation-before-effect |
| Enrollment definitively rejected | Staged key removed; no ready configuration | Not enrolled | Rejection and cleanup |
| Enrollment response lost or crash before commit | Staged key cleaned next command; inactive accepted enrollment remains unselectable and expires under the server rule | Not enrolled; fresh manual enrollment is allowed | Ambiguous and crash path |
| Ready-config atomic write fails | No ready configuration; staged material follows defined cleanup | Not enrolled or recovery-required, never enrolled | Atomic write failure |
| Startup registration fails | Listener stops; no ready publication | Nonzero exit | Startup failure |
| Temporary refresh fails | One bounded retry, never after expiry | Retrying before expiry | Fake-clock and retry cap |
| Definitive revocation/access failure | Serving stops; terminal state is published | Nonzero exit and recovery action | Definitive failure |
| Expiry | Serving stops even if cleanup has a separate error | Expired and nonzero exit | Boundary and shutdown race |
| Artifact fetch or validation fails | Incomplete staging is not committed as hit | Mode-specific unavailable or failure result | Hash/size/partial-store proof |
| Unknown artifact-grant key | One advertised refresh attempt, then fail closed | No cache artifact served | Grant-key rollover proof |

## 11. Non-functional, security, durability, and operations

- The listener binds only the exact enrolled endpoint after the process guard and local validation succeed.
- The process guard prevents a second local cache instance from touching key, store, listener, or registration state.
- Private keys remain in the selected OS-protected directory. Logs, JSON, diagnostics, OpenAPI examples, and error
  responses redact private material, grants, and opaque private references.
- The artifact store validates source hash and size before atomic complete-set publication. Cleanup is limited to
  abandoned staging and incomplete commits; scheduled retention is absent.
- Grant verification supports the already-advertised validation-key rollover. Cache service identity remains static;
  these key lifecycles have separate names, data, routes, and tests.
- Status is an operational contract. It must reflect current local protected state and current server liveness, not a
  historical successful run.
- The supported profile is one Linux x64 systemd deployment. The implementation rejects or declines unsupported host
  profiles instead of implying Windows or macOS support.

## 12. Proof strategy

| Requirement family | Implementation seam | Focused proof |
| --- | --- | --- |
| Immutable plans and mode contract | Materialization types, server resolver, OpenAPI, SDK/CLI projections | Positive root resolution; rejected whole-file/manifest/block cache requests; mode serialization; CacheRequired no-fallback; generated freshness. |
| Static identity pruning | Cache types, actor state, server routes, OpenAPI, SDKs, docs, test fixtures | Inventory finds no active service-identity rotation/candidate route, field, setting, state, or command; grant-key rollover remains proven. |
| R1 enrollment | Cache CLI/host boundary, protected key store, local config | Success; validation-before-effect; rejection; lost response; crash cleanup; atomic write failure; key mismatch; redaction; unsupported-profile rejection. |
| Resolved enrollment ambiguity | Completed #886 server enrollment and protected-identity proof | Inactive accepted enrollment is unselectable; fresh manual enrollment is allowed; server expiry eventually cleans it up; no automatic retry or reconciliation. |
| R2 liveness | Cache run host, registration route/actor, injectable clock/timer | Startup; exact endpoint; guard conflict; signed Unhealthy refresh; temporary retry; `Retry-After` cap; revocation; expiry; shutdown race; restart; server selection. #857 does not publish Healthy. |
| Artifact store and serving | SQLite/filesystem metadata, read-through fetcher, artifact route | Authorized hit; miss-fill-serve-hit; hash/size mismatch; partial staging; grant/proof rejection; grant-key rollover; restart completeness. |
| Working Directory Update | Cache execution path and #835 public local-update seam | Exact prepared content invokes `WorkingDirectoryUpdate.run`; no alternate local completion path. |
| Final integration | Epic branch, generated contracts, implementation audit | Current-head validation, focused integration tracer, generated artifacts, MarkdownLint, audit classification, and fresh review. |

Each implementation issue must run its Tier-2 research and the repository's issue-level Implementation Readiness Gate
against its own current branch head. The gate must name the invariant tuple, rejected shortcut shapes, positive,
negative, regression, boundary, adversarial cases where relevant, propagation surfaces, and specific N/A waivers. The
issue-level gate is not replaced by this Plan-ready topology.

For a behavior change, the leaf begins with a focused RED case when practical. Proof must include explicit public JSON
and wire-shape checks for changed contracts, then a generated OpenAPI/SDK freshness check. Cache-facing negative cases
include cross-repository selection, stale or revoked registration, stale grant, copied holder proof, wrong method or
route, incomplete local artifact, and CacheRequired attempted Direct fallback. A successful cache hit alone is not
proof of a safe cache path.

## 13. Requirements traceability ledger

| ID | Requirement | Status | Likely implementation seam | Proof seam | Planning owner | Residual risk |
| --- | --- | --- | --- | --- | --- | --- |
| GC-REQ-001 | Server-resolved immutable full-root plan | required | Materialization types/server/OpenAPI | Plan and resolver tests | Existing plan leaves | Cache must not become a resolver. |
| GC-REQ-002 | One static cache service identity | required | R0/R1A contracts and host config | Enrollment, config, redaction tests | #855, #886 | Local custody is OS-protected, not hardware-backed. |
| GC-REQ-003 | Resolved enrollment ambiguity disposition | implemented and proven | Completed registration and protected-identity seams | #886 and PR #888 proof | #886, PR #888 | Inactive accepted enrollment is unselectable; manual enrollment remains available without automatic recovery. |
| GC-REQ-004 | One bounded registration scheduler | required | R2 cache host and registration contract | Fake-clock transition tests | #857 | Exact response classes/time values require Tier-2 inventory. |
| GC-REQ-005 | Current eligible-cache selection | required | Server selection and registration actor | Selection/revocation/expiry tests | Existing and R2 | Snapshot is limited to plan issuance. |
| GC-REQ-006 | Authorized complete read-through | required | Store, artifact route, grant verifier | Miss-fill-serve-hit and negative integrity proof | #625 to #627 | Partial bytes must never become a hit. |
| GC-REQ-007 | Three execution-mode behaviors | required | Plan resolver, SDK, CLI, cache executor | Positive/negative mode and generated-contract proof | #625 to #630 | CacheRequired false fallback is high risk. |
| GC-REQ-008 | Working Directory Update sequence | required | Later local materialization | Cross-epic execution proof | #628 to #630 | Blocked until #835 merge and epic refresh. |
| GC-REQ-009 | Truthful status and diagnostics | required | #914 status and R2 process state | Status, redaction, expiry tests | #914, #857 | Must not report stale readiness. |
| GC-REQ-010 | Reset and redaction | required | Local config/key store and enrollment output | Failure and output scans | #905 | Manual recovery is intentional. |
| GC-REQ-011 | Static contract pruning | required | Shared types, routes, OpenAPI, SDK, docs | Inventory and freshness checks | #855 | Must retain artifact-grant key rollover. |
| GC-DEF-001 | Automatic identity rotation | deferred | None | Absence scan | Future Hardened epic | Do not leave a partial surface. |
| GC-DEF-002 | Prefetch and scheduled retention | deferred | None | Absence scan | Future work | Read-through remains the only correctness path. |
| GC-DEF-003 | Watch and Operations integration | deferred | None | Dependency-specific proof | #634 and later | Gates remain explicit. |

## 14. Planning handoff

### Value-bearing tracer

The first post-recovery tracer is one server-authorized immutable artifact request that misses a selected cache,
downloads the exact plan source, validates hash and size, commits atomically, serves bytes, and hits on the second
request. It crosses plan resolution, eligibility, grant verification, storage, serving, and a user-visible result
without rotation, prefetch, or scheduled retention.

### Selective salvage map

| Existing work | Product V1 disposition |
| --- | --- |
| PR #723 cache/AppHost skeleton | May be inspected and selectively recreated where compatible with this specification. |
| PR #723 rotation behavior | Do not merge or copy wholesale; it is superseded. |
| SQLite/store ideas | Audit independently against complete-artifact, integrity, and atomic-commit requirements. |
| Existing server registration foundation | Keep only rebaselined static enrollment, liveness, selection, and revocation rules. |
| Existing artifact-grant validation-key rollover | Preserve as the separate grant-verification capability. |
| Existing Direct and server plan-selection proof | Retain as current evidence, then extend through the cache tracer. |

### Candidate slices and dependencies

1. **R0 static-contract pruning (#855)**: Inventory every cache service-identity rotation/candidate surface, remove it or
   rebaseline it to static identity, regenerate contracts, and prove artifact-grant key rollover remains separate.
2. **R1A static enrollment identity (#886 / PR #888)**: Implemented and proven; its merged evidence establishes the
   completed static identity foundation.
3. **R1 record, status, and enrollment (#913, #914, #905)**: #913 reconciles the canonical record; #914 then owns
   pure local status only; #905 then owns one-shot enrollment only.
4. **R2 registration liveness (#857)**: Starts after #905 and its Tier-2 server timestamp, response-class, selection, and clock
   table are complete. Its signed refreshes remain Unhealthy; it does not publish Healthy.
5. **Runtime/artifact serving (#625)**: May proceed independently where write sets do not overlap with #835; it owns storage,
   grant verification, the miss-fill-serve-hit tracer, and Healthy publication only after serving readiness is proven.
6. **Cache-aware local execution and Watch**: #628 to #630 and #634 do not start until #835 merges to `main` and the
   Epic #597 branch refreshes from it.
7. **Final audit and release candidate**: Classify every row in `docs/Grace Cache implementation audit.md`, validate the
   current head, complete fresh review, and request Scott's explicit final merge approval.

Shared generated contracts, cache registration types, server route wiring, and later local materialization files are
high-conflict surfaces. Parallel work requires both independent user behavior and sufficiently disjoint write sets.

### Issue-readiness gates

- **R0:** Tier-2 static inventory covers DTOs, actor state/events, routes, CLI settings/status, local configuration,
  OpenAPI/SDK, documentation, tests, serializers, AOT roots, and command catalogs. It distinguishes cache service
  identity from artifact-grant validation keys.
- **R1 completion sequence:** #913 records the completed #886 / PR #888 disposition, #914 implements status only, and
  #905 implements enrollment only after #913 and #914. No leaf adds automatic retry or reconciliation.
- **R2:** Before code, produce the exact current registration response, timestamp, revocation/access, selection,
  capability, and clock transition table. It names one bounded retry schedule and its expiry cap.
- **All leaves:** Declare owned paths, forbidden paths, state/time model where needed, propagation dispositions, focused
  validation, documentation impact, and completion definition before editing.

### Review gate

Review uses the Product V1 contract plus a supported producer, supported reproduction, concrete likelihood, and
observable impact. The former numerical worker limit and cycle-three owner stop are revoked for #597. A repeated
invariant family requires a stabilization ledger, but does not itself stop implementation. Worker 5 receives the final
implementation or fix attempt. Supersession occurs only when the fresh review after worker 5 reports a valid,
addressable finding; reaching worker 5 alone does not supersede the pull request. The final epic-to-`main` merge is
never implied by checks or review; it requires Scott's explicit approval of the reviewed, validated current head.

## 15. Lifecycle audit, residual risks, and interruption triggers

### Lifecycle verdict

**Requested state:** Plan-ready

**Assessed state:** Plan-ready recovery topology

The specification is Plan-ready for implementation planning because Product V1 scope, deployment profile, required and
deferred capabilities, identity model, liveness shape, failure posture, propagation inventory, proof seams, tracer,
sequencing, and owner interruption rules are explicit. The recovery topology may now compile into focused issue plans.

This verdict does not make R2 or later semantic work ready to code. The portable specification contract says Plan-ready
supports implementation planning, while `dev-process` requires each issue to complete Tier-2 research and the issue-level
Implementation Readiness Gate. #913, #914, and #905 retain their declared docs, status, and enrollment boundaries; the
R2 exact liveness table remains a leaf-level gate.

### Passed criteria

- Owner decisions establish current Product V1 scope and remove the prior rotation capability.
- Current branch and live issue/PR state were inspected and drift is named rather than copied forward.
- Each required behavior has an implementation direction and focused proof direction.
- Every public, durable, generated, runtime, local, and documentation surface has a propagation disposition.
- The early miss-fill-serve-hit tracer and the #835 sequencing boundary are explicit.

### Known gaps and residual risks

- The requested project specification profile is absent at the required starting commit. Its absence is recorded for
  later repository restoration; this document does not infer missing profile rules.
- #914 status and #905 one-shot enrollment remain planned replacement leaves after #913's documentation correction.
- R2 still needs exact current server liveness response classes, timestamps, and selection timing on its branch head.
- Cache host, SQLite/filesystem implementation, artifact route execution, and the local Working Directory Update seam
  do not exist on this epic head. They are planned work, not present behavior.
- The selected host profile accepts operational limits outside Product V1: no platform parity, HA/DR, hostile-root
  defense, hardware-backed custody, or automatic reconciliation.

### Owner interruption triggers

Return to the owner before expanding scope when:

- R0 shows that pruning rotation changes the server source-of-truth model, needs a new durable object/state machine, or
  reveals a real deployed compatibility obligation.
- R2 needs a second scheduler/state machine, durable recovery workflow, new server source of truth, or generalized
  retry framework.
- A planned cache artifact cannot be represented as the full-root zip plus recursive metadata named by the server plan.
- #835 cannot supply `WorkingDirectoryUpdate.run` without conflicting local completion semantics.
- A requirement would add a deferred capability, an unsupported platform promise, or a new state machine beyond the
  Product V1 issue budget.

The easiest way to overbuild is to restore automatic rotation or reconciliation to handle an ambiguous enrollment or
refresh result. The correct Product V1 response is the declared fail-closed state, manual recovery, or owner direction.
The easiest way to under-test is to prove a cache hit without proving the validated miss-to-hit transition, the
CacheRequired no-fallback rule, or liveness at expiration.
