# Working Directory Update

**Status:** Plan-ready\
**Quality contract:** Product V1\
**Canonical source:** `docs/Working Directory Update.md`\
**Evidence current through:** 2026-08-11, `main` at `c477d09529b1bf0cc789d512c9e8c43731cda6f1`

## 1. Outcome

Working Directory Update is the Grace-controlled operation that makes an indexed working directory and its durable
local state match one selected server root. Branch switching, Watch current-Reference replay, and Connect retrieval use
one deep internal module for serialization, stale-state rejection, content verification, filesystem mutation, local
commit, cleanup, and truthful outcomes.

The module commits caller-specific progress only after it proves the selected root. Branch and Watch then perform an
idempotent finalization step. Connect commits its initial event cursor with the matching local status and object-cache
metadata.

## 2. Intent, scope, and non-goals

### Why this matters

Grace currently spreads working-directory mutation across large Branch, Watch, and Connect command implementations.
The shared `WorkingDirectoryMaterialization` module serializes arbitrary callbacks but does not own the transaction it
names. The result is duplicated planning, marker, verification, cancellation, and persistence behavior at the highest
local-integrity risk surface in the CLI.

Working Directory Update gives those callers one coherent contract while leaving their admission, target selection,
remote retrieval, scheduling, and presentation policies where they belong.

### Supported actors, workflows, and environments

- `grace switch` changing the selected branch and its working-directory root.
- `grace watch` replaying one current-branch Reference from server-ordered events.
- `grace connect` optionally retrieving its selected branch through the existing zip download.
- `grace doctor --repair-local-state` explicitly resolving a recorded incomplete finalization or reconstructing exact
  local state without changing working-directory content.
- Grace CLI on its currently supported local filesystem and SQLite environment. Windows path comparison remains
  supported; implementation must preserve existing platform abstractions for other supported systems.

### Required now

- One internal `WorkingDirectoryUpdate` module with `run` and `retryFinalization` operations.
- Caller-specific request constructors over one private normalized request.
- Object-cache, Connect zip, and deterministic prepared-content adapters.
- Repository-and-working-root scoped cross-process lease, marker, and completion sidecar.
- Exact selected-target and local-state revalidation after lease acquisition.
- Fresh planning, filesystem mutation, dual-hash verification, final-root verification, and one canonical SQLite
  completion transaction.
- The five outcomes `Unchanged`, `Updated`, `Rejected`, `UpdateIncomplete`, and `FinalizationIncomplete`.
- Idempotent Branch and Watch finalization, blocking while finalization is incomplete, and Doctor recovery.
- Human and machine-readable result projection with truthful nonzero exit status for incomplete outcomes.

### Deferred, rejected, and out of scope

- Automatic recovery for every interrupted phase.
- Filesystem rollback after mutation starts.
- Durable per-file planning or mutation journals.
- Multiple queued incomplete finalizations for one working directory.
- Hostile-local-process defense beyond file-lease and ownership-token correctness.
- Availability, failover, load, multi-host coordination, or broad cross-platform hardening beyond supported CLI use.
- Network reads while the update lease is held.
- Unbounded completion history or time-based record expiry.
- Compatibility, migration, or backfill for development-only local SQLite data. The schema may be replaced cleanly.
- A public planning or preview interface.
- A generic transaction-participant or SQL-callback extension mechanism.

## 3. Current-state evidence

| Evidence | Current behavior or contract | Relevance | Confidence or verification |
| -------- | ---------------------------- | --------- | -------------------------- |
| `src/Grace.CLI/Command/WorkingDirectoryMaterialization.CLI.fs` | A 75-line module provides an in-process lane and exclusive file lease around arbitrary callbacks. | The replacement must deepen this module rather than preserve the callback seam. | Current source. |
| `src/Grace.CLI/Command/Branch.CLI.fs` | Branch owns a workflow lease, Watch-clean preflight, marker handling, working-directory updates, local status, branch identity, and cache refresh. | Branch is the first public tracer and has a real post-update finalizer. | Current source around `runBranchSwitchWorkflowWithLease` and `updateWorkingDirectory`. |
| `src/Grace.CLI/Command/Watch.CLI.fs` | Watch has private plan/apply clients, an object-cache apply path, marker sidecars, serialized replay, and cursor acknowledgement. | Watch supplies the most demanding ordering and retry scenarios. | Current source around `CurrentBranchRemoteMaterializationPlan`, `applyCurrentBranchMaterializationTargets`, and cursor replay. |
| `src/Grace.CLI/Command/Connect.CLI.fs` | Connect writes configuration before optional retrieval, streams a server zip, writes working and object files together, and ignores undeclared zip files. | The design preserves zip retrieval while replacing extraction, verification, and outcome behavior. | Current source around `extractZipEntries` and `connectImpl`. |
| `src/Grace.CLI/LocalStateDb.CLI.fs` | Schema version 11 stores status, object-cache metadata, remote-reference boundaries, and bounded completion rows. Boundary writes require complete root identity matching. | A clean development-only schema replacement persists typed Branch selector facts and extends atomic commit operations. | Current source around `replaceStatusSnapshotWithRevisionCore` and cursor operations. |
| `src/Grace.CLI/Command/Doctor.CLI.fs` | `--repair-local-state` proves an unchanged working tree against server root and branch history before atomic reconstruction. | Doctor is the sole explicit recovery gesture and will gain recorded-finalization retry. | Current source around `repairLocalState`. |
| `src/Grace.Types/Reference.Types.fs` | `ReferenceMaterializationBoundaryDto` combines target root identity with an event cursor. | The internal update target must separate root identity from caller progress. | Current source at `ReferenceMaterializationBoundaryDto`. |
| `src/Grace.CLI/Grace.CLI.fsproj` | Connect currently compiles before the shared module. | The new module and adapters must compile before Connect, Branch, Watch, and Doctor. | Current project file. |
| ADR 0009 and ADR 0010 | Watch branch-transition refresh and current-branch materialization define existing caller-specific trust rules. | Working Directory Update must preserve and centralize their local mutation invariants without erasing caller policy. | Accepted repository decisions. |

The intended design deliberately supersedes current callback-based and Connect extraction behavior. It does not describe
those changes as already implemented.

## 4. Quality contract and accepted risk

The active profile is Product V1.

- **Data and compatibility:** Grace is not in production. Local schema replacement and fixture regeneration are
  allowed; no migration, dual read, or compatibility layer is required.
- **Integrity:** Working-directory bytes, object-cache bytes, selected root, local status, cursor, and completion state
  must never claim success without dual-hash and ordering proof.
- **Concurrency:** Branch, Watch, and Connect updates affecting the same local directory serialize across processes.
- **Recovery:** Exact same-operation retry, finalization-only retry, and explicit Doctor recovery are required. General
  rollback and automatic phase recovery are not.
- **Threat model:** Ordinary local process races, crashes, malformed prepared content, and stale selections are in
  scope. A malicious local process intentionally replacing coordination files is not.
- **Availability and scale:** No multi-host availability or load promises are added. Completion storage remains
  bounded.
- **Review and proof:** Real temporary filesystems and SQLite are required at the module seam. Public CLI output,
  ordering, cancellation, restart, and cleanup receive focused proof.
- **Complexity stop:** Do not add a durable running-operation state, per-file journal, rollback engine, generic plugin
  protocol, or recovery scheduler to satisfy this specification.

Accepted Product V1 risks include a random prepared zip remaining in the system temp directory after abrupt process
termination and manual intervention when interrupted bytes match no known server root.

## 5. Decisions and capability inventory

### Decision ledger

| ID | Lane | Status | Accepted decision | Implementation impact | Proof impact |
| -- | ---- | ------ | ----------------- | --------------------- | ------------ |
| DEC-001 | domain | accepted | Name the operation Working Directory Update. | Rename and deepen the shared module; add the term to `CONTEXT.md`. | Public and contributor docs use one term. |
| DEC-002 | scope | accepted | Product V1 is the quality contract; rollback, journals, broad automatic recovery, and hardening remain out of scope. | Keep the state machine bounded. | Prove selected V1 failure and restart paths only. |
| DEC-003 | architecture | accepted | Use a hybrid interface: generic `run` and `retryFinalization` operations with caller-specific request constructors. | One private engine serves Branch, Watch, and Connect. | All callers cross the same stable module seam. |
| DEC-004 | domain | accepted | Target identity is repository, branch, root DirectoryVersion, SHA-256, and BLAKE3; caller operation identity is separate and deterministic. | Do not use the event-boundary DTO as the internal target. | Same target/same operation and same target/new operation receive distinct proof. |
| DEC-005 | architecture | accepted | Prepared adapters expose immutable manifest entries and readable bytes, never mutation plans. | Zip, object-cache, and deterministic adapters share one content contract. | Adapter contract suites plus real module integration. |
| DEC-006 | architecture | accepted | Lease, marker, and sidecar use repository ID plus a normalized local-root-path hash, excluding branch identity. | Add a stable repository/root temp-scope helper. | Separate local roots do not block; branch changes do not move evidence. |
| DEC-007 | architecture | accepted | SQLite local completion is the irreversible point; the sidecar is derived Watch notification evidence. | Commit matching status, cache metadata, Connect cursor, and completion row atomically. | Crash tests at every commit and cleanup edge. |
| DEC-008 | product | accepted | `FinalizationIncomplete` blocks different updates until exact retry or Doctor recovery. | Persist one pending finalization and reject later operations. | Ordering and blocking tests across processes. |
| DEC-009 | architecture | accepted | SQLite stores no pre-completion operation state and retains only the latest terminal row per caller plus one pending row. | No running row, operation log, or expiry clock. | Retention and supersession proof. |
| DEC-010 | product | accepted | Doctor first retries recorded finalization without filesystem mutation, then may perform exact reconstruction. | Persist minimum typed finalization facts and extend Doctor. | Branch, Watch, unknown-marker, and refusal cases. |
| DEC-011 | product | accepted | Connect keeps zip download, stages locally, validates exact manifest coverage, writes verified objects first, and performs no network reads under lease. | Replace `extractZipEntries`; add zip adapter and local staging. | Per-file triple-location dual-hash and malformed-archive proof. |
| DEC-012 | product | accepted | Connect `--force` replaces conflicts only at target paths and never deletes unrelated eligible content. | Caller admission supplies approved target conflicts; module rejects unexpected paths. | Force, no-force, unrelated, ignored, and path-type tests. |
| DEC-013 | product | accepted | Connect configuration persists before optional retrieval and output reports configuration separately from update result. | Preserve config ordering; extend Connect result projection. | Retrieval-disabled and failed-update behavior. |
| DEC-014 | architecture | accepted | The same deterministic operation may adopt a known orphaned marker after lease acquisition and fresh replanning. | Separate operation ID from random attempt token. | Same, different, malformed, and completed marker tests. |
| DEC-015 | product | accepted | Incomplete outcomes use nonzero exit status; `FinalizationIncomplete` says bytes were updated and recommends Doctor. | Add typed human and machine projections. | Exit-code, JSON, and no-mixed-output proof. |
| DEC-016 | proof | accepted | `grace switch` is the first value-bearing tracer. | Build the core module through one object-backed Branch path before Watch and Connect migration. | Public tracer proves the shared transaction and finalization. |
| DEC-017 | product | accepted | A Branch request selected by branch name, branch ID, or Reference uses `Reference(referenceId)` and may transition branch identity. A request selected by SHA-256 or BLAKE3 uses `DirectoryVersion`, bound by its exact target root; it keeps the current branch identity and has no Reference ID. | Persist selector kind and the optional Reference ID, and reconstruct the same typed facts for retry. Replace the development-only local schema cleanly at version 11. | Prove both selector forms, including equivalent hash prefixes resolving to one operation and reopen of both persisted shapes. |

### Capability inventory

| Capability | Disposition | User-visible outcome | Surfaces | Intrinsic obligations | Complexity impact | Reason |
| ---------- | ----------- | -------------------- | -------- | --------------------- | ----------------- | ------ |
| Shared update transaction | Required now | Three callers report the same truthful outcomes. | CLI internals, filesystem, SQLite | Serialization, revalidation, integrity, cleanup | Replaces duplicated logic | Core value. |
| Branch finalization | Required now | Selected branch identity follows verified bytes. | Branch CLI, config, SQLite | Idempotency and blocking | Small typed finalizer | Required by tracer. |
| Watch finalization | Required now | Cursor advances only after verified bytes. | Watch replay, SQLite, IPC status | Ordered replay and idempotency | Small typed finalizer | Required current behavior. |
| Connect zip adapter | Required now | Existing zip retrieval produces verified object and working bytes. | Connect CLI, temp zip, object cache | Exact manifest and dual hashes | Dedicated adapter | Required current workflow. |
| Doctor recovery | Required now | One documented command resolves exact recoverable states. | Doctor CLI, SQLite, marker | No file mutation, exact proof | Extends existing repair | Required operator path. |
| Filesystem rollback | Rejected | None promised. | Not applicable | Would require durable inverse plans | High | Outside Product V1. |
| Per-file durable journal | Rejected | None promised. | Not applicable | Restart and compaction lifecycle | High | Marker plus exact retry is sufficient. |
| Automatic general recovery | Deferred | Manual Doctor remains required for ambiguous states. | Future maintenance | Classification and scheduling | High | No V1 value beyond selected paths. |
| Temp-file scavenging after crashes | Deferred | Handled exits clean up; abrupt exits may leave random zip files. | System temp | Age, ownership, and deletion safety | Moderate | Accepted Product V1 risk. |
| Hostile local coordination-file defense | Out of scope | No promise. | Local temp files | Identity and access hardening | High | Not in current threat model. |
| Public update-planning interface | Rejected | No preview or plan manipulation. | CLI/API | Stable plan format and compatibility | High | Would make the module shallow. |

## 6. Domain language and model

### Canonical operation term

A Grace-controlled operation that changes indexed working-directory content and durable local state to match one
selected server root, then commits caller-specific progress only after verifying that match.

### Selected target

The immutable tuple of RepositoryId, BranchId, root DirectoryVersionId, root SHA-256, and root BLAKE3 selected by the
caller. An event cursor is not part of the target.

### Operation identity

The deterministic caller-specific tuple that distinguishes retry, stale work, and a new operation. Grace hashes a
canonical serialization of the tuple. Correlation IDs are diagnostic and do not affect idempotency.

- Watch: caller kind, repository, branch, and exact event cursor.
- Branch `Reference(referenceId)`: caller kind, repository, previous branch, selected branch, selected Reference, and target root.
- Branch `DirectoryVersion`: caller kind, repository, current branch, selector kind, and target root. SHA-256 and BLAKE3
  prefixes that resolve to that same exact root are the same operation.
- Connect: caller kind, repository, selected branch, target root, initial cursor, and local root scope.

### Attempt token

A random token identifying one execution attempt. It owns one marker instance and is never used as logical operation
identity.

### Prepared content

An immutable target manifest plus readable uncompressed file bytes. Its adapter owns zip, object-cache, or test
resources and is disposed by the module. It cannot supply a mutation plan.

### Local completion

The SQLite transaction that atomically records matching status, required object-cache metadata, Connect's initial
cursor when applicable, and the operation completion row. This is the irreversible point.

### Finalization

An idempotent caller action performed after local completion while the lease remains held. A Reference-selected Branch
persists the selected branch identity. A DirectoryVersion-selected Branch verifies that the current branch remains
unchanged and never publishes branch identity. Watch advances the exact cursor. Connect ordinarily has no separate
finalization.

### Local root scope

The lowercase SHA-256 of the normalized absolute working-directory path, combined with repository identity to scope
lease, marker, and sidecar files. It is not a DirectoryVersion identity.

## 7. Supported scenarios and workflows

### Successful Branch tracer

Branch completes admission and content preparation, constructs a deterministic request, and calls `run`. A branch or
Reference selection creates `Reference(referenceId)`; a SHA-256 or BLAKE3 selection creates `DirectoryVersion` bound
to the resolved target root. The module
acquires the lease, revalidates selection and clean local state, plans from current bytes, marks, applies from verified
objects, verifies the target, commits local completion, removes the marker, and finalizes selected branch identity.
The Reference form transitions branch identity. The DirectoryVersion form keeps the current branch identity. The command
returns `Updated` with exit code 0.

### Already-matching target

If current working bytes and status already match the target, the module performs no filesystem mutation. The same
operation returns its existing completion; a new operation receives its own local completion and finalization.

### Branch outcome projection

`grace branch switch` projects `Updated` and `Unchanged` as successful human text or a single
`GraceReturnValue<string>` JSON envelope, with exit code `0`. `Rejected`, `UpdateIncomplete`, and
`FinalizationIncomplete` project their outcome name and reason through the normal human error or `GraceError` JSON
envelope, with a nonzero exit code. `FinalizationIncomplete` states that working-directory bytes were updated and
recommends exactly `grace doctor --repair-local-state`.

### Watch replay

Watch retains replay admission and server ordering. The module applies one selected Reference from verified object
content. Only after local completion does the finalizer advance the expected cursor to the accepted cursor. A newer
event selecting the same root is a new operation and still finalizes its cursor.

### Connect retrieval

Connect persists valid configuration, resolves target and cursor, downloads the complete zip locally, validates exact
manifest coverage and hashes, and calls `run`. Under the lease, the module verifies or creates object files first,
copies verified objects into approved target paths, verifies the final root, and commits status, cache metadata, initial
cursor, and completion together.

### Connect without retrieval

`--retrieve-default-branch false` persists configuration and does not call the module, establish a materialized root,
or record an event cursor.

### Local conflict

Without `--force`, Connect rejects a mismatching selected target path before mutation. With `--force`, it may replace
that path. Eligible content outside the target is always rejected. Ignored content remains untouched.

### Failure after mutation

After the first filesystem mutation, cancellation is deferred. If verified local completion cannot be reached, the
module returns `UpdateIncomplete`, records no completion row, and removes only its owned marker on handled failure.
A retry revalidates and creates a new plan.

### Finalization failure

The module commits local completion, writes derived notification evidence, removes its marker, and attempts finalization.
Failure returns `FinalizationIncomplete`, a nonzero exit, and a Doctor recommendation. A different update is blocked.

### Same-operation restart

After acquiring the lease, the same deterministic operation may adopt a known orphaned marker. It never continues an
old plan; it validates content and current state and plans again. A different or unrecognized marker is rejected and
requires Doctor.

### Doctor recovery

Doctor acquires the update lease. For a pending finalization, it proves the recorded target and retries only the typed
idempotent finalizer. If that is not applicable, Doctor may use its existing exact working-tree reconstruction path.
It never changes working-directory content and preserves evidence when proof fails.

## 8. Functional requirements and invariants

### REQ-001 — One deep internal module

- Branch, Watch, and Connect must cross `WorkingDirectoryUpdate.run` for working-directory mutation.
- Finalization-only recovery must cross `WorkingDirectoryUpdate.retryFinalization`.
- Callers retain admission, target selection, remote access, scheduling, and rendering.
- Callers must not pass mutation plans, filesystem writers, database handles, or transaction callbacks.

### REQ-002 — Complete target and operation identity

- Target identity must contain repository, branch, root DirectoryVersion, SHA-256, and BLAKE3.
- Operation identity must be deterministic from the accepted caller tuple.
- Marker attempt tokens must be random and independent from operation identity.
- Empty, incomplete, mismatched, or noncanonical identity must be rejected before mutation.

### REQ-003 — Exact prepared content

- Prepared content must cover every target file exactly once and no undeclared non-directory content.
- Paths must be normalized, relative, representable, contained, and collision-free under active path comparison.
- Each uncompressed file must match its declared SHA-256 and BLAKE3 before lease acquisition.
- The adapter must be disposed on every terminal path.

### REQ-004 — Stable serialization scope

- The lease, marker, and sidecar must use repository identity plus local root scope and exclude branch identity.
- Only one update may hold the scope lease.
- A caller must revalidate selected target, configuration, local status, pending finalization, and relevant filesystem
  state after acquiring the lease and before mutation.

### REQ-005 — Versioned owned marker

- The marker must contain schema version, attempt token, caller kind, operation identity, repository and branch facts,
  target root, start time, and diagnostic process ID.
- It must not contain credentials, zip URLs, or unnecessary absolute paths.
- Cleanup must remove only the marker matching the current attempt token.
- Malformed, unsupported, or different-operation markers require Doctor.

### REQ-006 — Fresh plan and local-content safety

- The module must build the mutation plan only after lease acquisition and revalidation.
- The plan may delete only paths proven by accepted prior status to be Grace-tracked and absent from the target.
- Unexpected eligible content must reject the operation.
- Connect `--force` may replace only conflicts at selected target paths.
- Ignored content must remain untouched and outside selected-root comparison.

### REQ-007 — Verified object-first application

- Connect must complete its zip download and validation before lease acquisition.
- No remote access may occur while the lease is held.
- Every object file must be dual-hash verified before use; a mismatched existing object must be replaced from validated
  content through an atomic local-file publication step.
- Working files must be copied only from verified objects and verified again after copy.

### REQ-008 — Final-root verification

- The module must independently verify retained paths, applied files, directory structure, and both final root hashes.
- The selected DirectoryVersion identity must be used only when computed root hashes match the selected target.
- No durable status or completion record may be written for an unverified root.

### REQ-009 — Canonical local completion

- One SQLite transaction must commit matching status, required cache metadata, Connect's initial cursor when present,
  and the operation completion row.
- No SQLite operation row may exist before this transaction.
- The sidecar is derived notification evidence and cannot override SQLite truth.
- A crash after commit must remain discoverable as locally complete even if sidecar or marker cleanup did not finish.

### REQ-010 — Bounded completion state

- The table must retain the single pending finalization and the latest terminal row per caller kind.
- Pending finalization must never be pruned or superseded.
- A newer terminal operation may prune the older terminal row for the same caller.
- No time-based expiry or unbounded history is allowed.

### REQ-011 — Idempotent finalization and blocking

- Branch and Watch finalizers must be idempotent for the operation identity.
- Initial finalization occurs while the lease remains held.
- A pending finalization blocks every different update in the same scope.
- Finalization retry must reacquire the lease, prove the same target, perform no filesystem mutation, and retry only the
  finalizer.

### REQ-012 — Truthful outcomes and exit status

- `Unchanged` and `Updated` are success outcomes with exit code 0.
- `Rejected`, `UpdateIncomplete`, and `FinalizationIncomplete` are nonzero outcomes.
- `FinalizationIncomplete` must state that working-directory bytes were updated and recommend
  `grace doctor --repair-local-state`.
- Human and JSON output must distinguish configured Connect identity from update outcome.
- Parse, authentication, and transport errors remain normal `GraceError` failures rather than update outcomes.

### REQ-013 — Cancellation and progress

- Cancellation is honored during admission, download, preparation, lease waiting, and before first mutation.
- After first mutation, cancellation is deferred until verified local completion or unavoidable update failure.
- Cancellation may apply again during finalization and produce `FinalizationIncomplete`.
- Progress is coarse: preparing, waiting, applying, verifying, committing, and finalizing.
- Progress failure cannot control or fail the transaction.

### REQ-014 — Same-operation marker adoption

- Adoption is allowed only after acquiring the lease and matching marker schema, operation identity, scope, and target.
- A matching completion row routes to cleanup and finalization-only retry.
- No completion row routes to complete revalidation and fresh planning.
- Adoption replaces the old attempt token; it never resumes an old plan.

### REQ-015 — Explicit Doctor recovery

- Doctor must use the same update lease and refuse active competing work.
- It must first attempt an exact filesystem-free retry of a recorded pending finalization.
- Fallback reconstruction must preserve current bytes and prove a complete known server root and event boundary.
- Doctor must not guess targets, resume archive extraction, roll back content, or delete unrecognized evidence without
  exact reconciliation.

### REQ-016 — Caller-specific ordering

- Branch must not publish selected branch identity before local completion.
- Watch must not advance its cursor before local completion and must preserve ordered replay.
- Connect must not write an initial cursor without matching status and root.
- Connect configuration remains independently valid when retrieval is not requested or does not complete.

### REQ-017 — Public and contributor documentation

- The canonical specification and ADR must define the current intended contract.
- CLI help, Doctor documentation, machine-readable output documentation, Watch documentation, and `CONTEXT.md` must be
  updated with implementation.
- Design-only cross-references must not claim behavior is already implemented.

## 9. Interfaces, contracts, and propagation

### Internal interface shape

The implementation may refine names while preserving this shape:

```fsharp
module internal WorkingDirectoryUpdate =
    type Target = private Target of repositoryId: RepositoryId * branchId: BranchId * root: RootIdentity
    type Operation = private Operation of string
    type Request = private Request
    type FinalizationRequest = private FinalizationRequest

    type Outcome =
        | Unchanged of Receipt
        | Updated of Receipt
        | Rejected of Failure
        | UpdateIncomplete of Failure
        | FinalizationIncomplete of Receipt * Failure

    val run: Request -> CancellationToken -> Task<Outcome>
    val retryFinalization: FinalizationRequest -> CancellationToken -> Task<Outcome>
```

`Request.branchSwitch`, `Request.watchReplay`, and `Request.connectBootstrap` are constructors, not separate transaction
implementations. `IPreparedContent`, selected-target reading, and idempotent finalization are the only caller-facing
variation seams.

### Contract propagation matrix

| Surface | Owner or artifact | Accepted and rejected values | Persistence impact | Compatibility posture | Disposition | Proof |
| ------- | ----------------- | ---------------------------- | ------------------ | --------------------- | ----------- | ----- |
| Internal module | New `WorkingDirectoryUpdate*.CLI.fs` files | Private typed requests and five outcomes; reject incomplete identity and arbitrary callbacks | None directly | Clean replacement | Updated | Shared integration suite. |
| F# compile order | `Grace.CLI.fsproj` | Core and adapters precede all consumers | None | No compatibility concern | Updated | Release build. |
| SQLite | `LocalStateDb.CLI.fs` | Bounded completion rows and typed finalization facts | Schema replacement from development schema 9 | No migration or dual read | Updated | Real SQLite schema, transaction, restart, and retention tests. |
| Coordination files | Services and update module | Versioned known marker; reject malformed or conflicting marker | Temp lease, marker, sidecar | Replace branch-scoped marker | Updated | Cross-process and restart tests. |
| Branch CLI | `Branch.CLI.fs`, output DTO/registry | Typed update outcomes; Branch finalizer | Selected branch follows local completion | Current behavior replaced cleanly | Updated | `grace switch` tracer, JSON, exit-code tests. |
| Watch | `Watch.CLI.fs`, existing check projection | Internal outcomes; foreground Watch remains continuous | Cursor finalization and existing IPC status | Preserve existing event and check shapes unless proof requires additive reason text | Updated | Ordered replay, same-root event, blocked status, restart tests. |
| Connect CLI | `Connect.CLI.fs`, `ConnectDto` or replacement projection | Exact zip; target-only `--force`; optional retrieval | Config separate; status/cache/cursor/completion atomic | Clean current-contract replacement | Updated | Zip, force, no-retrieval, JSON, and exit-code tests. |
| Doctor CLI | `Doctor.CLI.fs`, `DoctorReportDto` | Exact finalization retry or exact reconstruction only | Finalized/recovered completion status | Extend current explicit option | Updated | Pending Branch/Watch, refusal, and JSON tests. |
| Machine output registry | `CommandOutputContract.CLI.fs` | Stable outcome DTOs; invalid command shape remains `GraceError` | None | `branch.switch` leaves current V2 deferral when implementation lands | Updated | Schema, example, select, single-document, and exit tests. |
| Shared Types and SDK | `Grace.Types`, `Grace.SDK` | Existing server target and cursor DTOs remain | None | No change | Unchanged with reason | Build and scoped diff. |
| HTTP/OpenAPI/generated clients | Server routes and generated artifacts | No new route or wire behavior | None | No change | Not applicable with reason | Generated freshness and scoped diff. |
| Configuration | Grace local config | Connect identity may exist without retrieved root | Existing config file | Preserve supported no-retrieval behavior | Updated documentation; runtime ordering preserved | Connect scenarios. |
| Documentation | This specification, ADR 0011, context and user docs | One canonical term and planned/current distinction | None | Update with implementation | Updated | MarkdownLint and review. |

## 10. Source, identity, state, time, and outcomes

### Source and identity model

| Concern | Contract |
| ------- | -------- |
| Selected target source | Caller-selected immutable server Reference/root facts, reread through the caller's typed selected-target seam. |
| Current local source | Fresh configuration snapshot, SQLite status and completion rows, held scope lease, marker, and actual filesystem bytes. |
| Same operation | Equal deterministic caller tuple within the same repository/root scope. |
| Same target, new operation | Equal target tuple but different deterministic operation tuple; no file mutation, new completion/finalization. |
| Stale work | Selected target, configuration, prior status, expected cursor, or branch facts differ at post-lease revalidation. |
| Conflicting work | Another pending finalization, a different known marker, or unexpected eligible filesystem content. |
| Attempt ownership | Held lease plus exact random token in the current marker. Process ID is diagnostic only. |
| Revalidation point | After lease acquisition and again immediately before mutation or finalization-only commit. |

### State model

| State | Entry condition | Durable truth | Allowed transitions | Terminal? | Restart behavior | Published result |
| ----- | --------------- | ------------- | ------------------- | --------- | ---------------- | ---------------- |
| Preparing | Caller is selecting and preparing content. | No update row. | Waiting, Rejected | No | Caller restarts preparation. | Coarse progress only. |
| Waiting | Valid request is waiting for scope lease. | No update row. | Applying, Rejected | No | Caller resubmits. | Coarse progress only. |
| Applying | Lease held, revalidation passed, owned marker written, first mutation may occur. | Marker only. | Verifying, UpdateIncomplete | No | Same operation may adopt known marker and replan. | No success publication. |
| Verifying | Planned writes completed; content and root are being proven. | Marker only. | Committing, UpdateIncomplete | No | Same as Applying. | No success publication. |
| Committing | Verified root is entering canonical SQLite transaction. | Marker; row appears only on successful commit. | FinalizationPending, UpdateIncomplete | No | Presence of completion row decides the path. | No success publication. |
| FinalizationPending | Local completion transaction committed. | Completion row with finalization pending. | Finalized, RecoveredByDoctor, FinalizationIncomplete | No | Retry only finalization after exact proof. | Incomplete result if finalizer fails. |
| Finalized | Required finalization completed. | Latest terminal row for caller plus caller progress. | Superseded by newer same-caller terminal row | Yes | Same operation returns existing completion. | `Updated` or `Unchanged`. |
| RecoveredByDoctor | Doctor explicitly reconciled exact local state. | Terminal recovery row and exact local state. | Superseded by newer same-caller terminal row | Yes | Normal later operations may proceed. | Doctor recovery result. |
| Rejected | No mutation began. | No update row. | Caller fixes reason and resubmits. | Yes | Nothing to recover unless foreign marker remains. | `Rejected`. |
| UpdateIncomplete | Mutation began but local completion did not commit. | No completion row; handled marker removed, crash marker may remain. | Fresh same-operation retry or Doctor | Yes | Revalidate and replan. | `UpdateIncomplete`. |
| FinalizationIncomplete | Local completion committed but caller progress did not finish. | Pending completion row. | Same-operation finalization retry or Doctor | Yes | Blocks different operations. | `FinalizationIncomplete`. |

No state has an expiry timer. Timestamps use UTC for diagnostics and completion evidence; they do not decide operation
ownership, adoption, or pruning.

### Side-effect ordering

```text
prepare and validate content
→ acquire scope lease
→ inspect completion and marker state
→ revalidate target and local state
→ build fresh plan
→ create or adopt owned marker
→ verify or publish object files atomically
→ mutate approved working-directory paths
→ verify applied files and final root
→ atomically commit status + cache metadata + Connect cursor + completion row
→ write derived completion sidecar
→ remove owned marker
→ run idempotent Branch or Watch finalization
→ mark completion terminal
→ release lease
→ dispose prepared content
```

### Failure and outcome matrix

| Outcome | Filesystem mutation | Durable truth | Retry | Cleanup and user result |
| ------- | ------------------- | ------------- | ----- | ----------------------- |
| `Unchanged` | None | Matching terminal completion and caller progress | Safe same-operation replay | Exit 0. |
| `Updated` | Completed and verified | Matching terminal completion and caller progress | Safe same-operation replay | Exit 0. |
| `Rejected` | None | No completion row | Fix classified reason; Doctor for unrecognized marker | Remove only owned marker; nonzero. |
| `UpdateIncomplete` | Began or may have begun | No completion row | Revalidate and replan; same operation may adopt crash marker | Nonzero; never claim matching root. |
| `FinalizationIncomplete` | Completed and verified | Pending completion row | Finalization-only retry or Doctor | Nonzero; say bytes updated and recommend Doctor. |

### Cancellation and cleanup

Cancellation is active through preparation, lease wait, and the final pre-mutation check. Once the first filesystem
mutation begins, the module defers cancellation until it reaches verified local completion or unavoidable failure. The
token may apply again to finalization. Prepared content is disposed after lease release on every handled path. Marker
and sidecar deletion never erase SQLite completion truth.

## 11. Non-functional, security, durability, and operations

- Hash every accepted uncompressed file with SHA-256 and BLAKE3 during preparation, object publication, and final
  working-file verification as applicable.
- Publish object files through temporary files and atomic local replacement so no reader observes partially written
  content at its expected object filename.
- Do not log file contents, credentials, signed download URLs, or unnecessary absolute paths.
- Keep remote access outside the update lease. Target rereads required for pre-mutation validation must be bounded and
  occur before the marker or first mutation; if the caller cannot perform that reread, reject rather than use stale
  selection.
- Use real exclusive file handles for cross-process serialization and the existing in-process lane only as a local
  efficiency measure.
- Treat progress as optional presentation. Progress callbacks cannot change ordering or outcomes.
- Preserve one JSON document on stdout in JSON mode; human progress and diagnostics stay off JSON stdout.
- Development databases and test fixtures may reset for the new schema. Documentation must not promise migration.
- Operational recovery is explicit. Doctor refuses when exact proof cannot be established and preserves evidence.

## 12. Proof strategy

| Invariant | RED evidence | Positive proof | Negative and boundary proof | Stable seam |
| --------- | ------------ | -------------- | --------------------------- | ----------- |
| One shared transaction | Current callers bypass the callback-only module. | Branch tracer, Watch, and Connect all call `run`. | Scoped source test rejects direct mutation entry points after retirement. | Internal module plus public caller tests. |
| Post-lease revalidation | Current preparation and mutation checks are distributed. | Target and local facts reread after held lease. | Change selection, status, marker, path, or cursor while waiting; assert no mutation. | Real temp filesystem and SQLite. |
| Prepared-content integrity | Current Connect ignores extra entries and does not verify object bytes. | Exact zip and object adapters pass dual hashes. | Missing, extra, duplicate, unsafe, corrupt, case-colliding, file/directory-swap content rejects. | Adapter contract suite. |
| Object-first verified apply | Current Connect writes working and object paths together. | Objects verify before working copies. | Corrupt existing object, interrupted object write, changed object before copy. | Zip adapter plus real files. |
| Final-root truth | Current callers use different verification paths. | Every caller commits selected dual-hash root. | Mutate retained/applied file before commit; assert no completion row. | Module integration. |
| Atomic local completion | Completion row does not exist today. | Status, cache, Connect cursor, and completion appear together. | Failure before commit leaves none; crash after commit remains locally complete. | Real SQLite restart tests. |
| Finalization blocking | No shared pending-finalization state exists today. | Same operation retries idempotently. | Different caller and operation reject while pending; finalizer success before row update safely repeats. | Module and Doctor integration. |
| Marker adoption | Current markers are text and branch-scoped. | Same operation adopts after lease and replans. | Different target, operation, token, schema, or malformed file requires Doctor. | Cross-process lease tests. |
| Cancellation | Current Connect checks cancellation after extraction. | Pre-mutation cancellation stops cleanly; post-mutation cancellation defers. | Cancel at object, first working write, verification, commit, and finalization seams. | Deterministic failure seams around real operations. |
| Doctor recovery | Current Doctor reconstructs only current configured branch state. | Recorded Branch and Watch finalization retries without file mutation. | Changed bytes, config, target, active Watch, unknown marker, or SQLite generation refuses. | Doctor CLI integration. |
| CLI truth | Current Branch JSON remains deferred and no shared DTO exists. | Outcome DTOs, exit codes, and Doctor recommendation match. | No mixed JSON output; transport errors remain `GraceError`; Watch continuous mode unchanged. | Built executable help/schema/examples and CLI tests. |
| Retention | No completion rows exist today. | Latest terminal per caller retained. | Pending never pruned; newer same-caller terminal removes only older terminal. | SQLite tests. |

The repository's focused Release build/test profile is the expected implementation proof. Broad Fast or Full validation
is selected only under repository guidance; required pull-request `Validate` remains the broad current-revision gate.

## 13. Requirements traceability ledger

| ID | Requirement | Source | Type | Status | Implementation seam | Proof seam | Candidate planning owner | Residual risk |
| -- | ----------- | ------ | ---- | ------ | ------------------- | ---------- | ------------------------ | ------------- |
| REQ-001 | One deep internal module | DEC-003 | behavior | Required | `WorkingDirectoryUpdate.CLI.fs` | Branch/Watch/Connect integration | Core/tracer slice | Interface could regain callback breadth. |
| REQ-002 | Complete target and operation identity | DEC-004, DEC-014 | contract | Required | Request constructors and marker serializer | Identity and replay tests | Core/tracer slice | Canonical tuple encoding must be stable. |
| REQ-003 | Exact prepared content | DEC-005, DEC-011 | security | Required | Prepared adapters | Shared adapter contract tests | Adapter slices | Zip format assumptions require source fixtures. |
| REQ-004 | Stable serialization scope | DEC-006 | non-functional | Required | Services scope helper and lease | Cross-process scope tests | Core/tracer slice | Platform path normalization drift. |
| REQ-005 | Versioned owned marker | DEC-006, DEC-014 | behavior | Required | Marker store | Ownership, schema, crash tests | Core/tracer slice | Filesystem notification ordering differs by platform. |
| REQ-006 | Fresh plan and local-content safety | DEC-012 | security | Required | Planner and caller admission facts | Conflict, race, delete-scope tests | Core/tracer and Connect slices | Existing ignore semantics are complex. |
| REQ-007 | Verified object-first application | DEC-011 | behavior | Required | Zip/object adapters and apply engine | Corrupt object and copy tests | Connect slice | Triple hashing costs local I/O. |
| REQ-008 | Final-root verification | DEC-004, DEC-007 | behavior | Required | Verifier | Changed retained/applied path tests | Core/tracer slice | Large-tree test cost. |
| REQ-009 | Canonical local completion | DEC-007, DEC-009 | contract | Required | `LocalStateDb` transaction | Atomicity and restart tests | Core/tracer slice | SQLite schema fans into many tests. |
| REQ-010 | Bounded completion state | DEC-009 | non-functional | Required | Completion row retention | Retention tests | Core/tracer slice | Latest-per-caller assumption depends on caller ordering. |
| REQ-011 | Idempotent finalization and blocking | DEC-008, DEC-010 | behavior | Required | Finalizer and completion store | Branch/Watch retry and blocking tests | Branch, Watch, Doctor slices | Finalizer reconstruction facts must stay minimal. |
| REQ-012 | Truthful outcomes and exit status | DEC-015 | contract | Required | Common DTOs, renderers, registry | JSON, human, exit, schema tests | Output/Doctor slice | Current Branch JSON deferral must be removed coherently. |
| REQ-013 | Cancellation and progress | DEC-002 | behavior | Required | Core engine | Phase cancellation tests | Core/tracer slice | Timing seams can become over-injected. |
| REQ-014 | Same-operation marker adoption | DEC-014 | workflow | Required | Core engine and marker store | Crash/adoption tests | Core/tracer slice | Adoption must never skip fresh planning. |
| REQ-015 | Explicit Doctor recovery | DEC-010 | workflow | Required | Doctor and retry interface | Exact recovery/refusal tests | Doctor slice | Ambiguous bytes remain manual by design. |
| REQ-016 | Caller-specific ordering | DEC-007, DEC-008, DEC-013 | behavior | Required | Branch, Watch, Connect constructors/finalizers | Caller ordering tests | Caller slices | Existing caller code is large and intertwined. |
| REQ-017 | Public and contributor documentation | DEC-001, DEC-015 | documentation | Required | Approved docs | MarkdownLint and review | Final audit slice | Docs can drift during staged migration. |

No required row lacks an implementation or proof seam.

## 14. Implementation-planning handoff

### Value-bearing tracer

The first tracer is `grace switch` between two branches when required target objects are already prepared. It must cross
the public command, generic request, lease and marker, real filesystem, SQLite completion, selected-branch finalizer,
typed output, and focused proof. A controlled finalizer failure must prove blocking and Doctor recovery before the
tracer is considered complete.

### Likely vertical slices and dependencies

1. **Core plus Branch tracer:** schema replacement, compile-order scaffold, shared module, object adapter, stable
   coordination scope, Branch request/finalizer, five outcomes, and tracer proof.
1. **Watch integration:** replace current-reference plan/apply internals with the shared module while preserving replay,
   IPC, SignalR, and cursor policy.
1. **Connect integration:** local zip staging, exact adapter validation, object-first apply, target-only `--force`,
   configuration/result separation, and atomic initial cursor.
1. **Doctor and public output:** finalization retry, exact fallback recovery, outcome DTOs, help, schema/examples, exit
   behavior, and Watch blocked diagnostics.
1. **Consolidation and audit:** remove superseded helpers and internal-hook tests after parity, finish docs, run contract
   propagation review, and record final proof.

The first slice may include a small compile-item scaffold before semantic work, but it must still deliver the complete
Branch tracer rather than stopping at abstractions.

### High-conflict and shared surfaces

- `src/Grace.CLI/Grace.CLI.fsproj`
- `src/Grace.CLI/LocalStateDb.CLI.fs`
- `src/Grace.CLI/Command/Services.CLI.fs`
- `src/Grace.CLI/Command/Common.CLI.fs`
- Shared CLI test project files and helpers
- This specification and ADR 0011

Slices touching these surfaces should be serialized or intentionally integrated. Watch and Connect migration should
not overlap when both edit the shared module or SQLite schema.

### Contract propagation obligations

- Promote `branch.switch` from its current machine-output deferral only when its stable outcome DTO is implemented.
- Keep server, SDK, static OpenAPI, and generated clients unchanged unless current implementation evidence reveals a
  genuine cross-project dependency.
- Replace the local SQLite schema cleanly and regenerate fixtures; do not add migration code.
- Update help and current-behavior docs in the same slice that changes their command behavior.
- Preserve ADR 0009 and ADR 0010 caller-specific decisions and link them to ADR 0011.

### Owner-interruption triggers

Return to the owner before adding:

- Any sixth public outcome or another durable update state.
- More than one pending finalization per working directory.
- A new public command, flag, server route, SDK method, or OpenAPI shape.
- Filesystem rollback, a per-file journal, automatic recovery scheduling, or time-based expiry.
- Permission for `--force` to delete unrelated content.
- Network I/O under the update lease.
- A quality-contract change from Product V1.

## 15. Completion semantics and self-critique

The feature is complete only when every required traceability row is implemented and proven or explicitly returned to
the owner. A partially migrated caller does not satisfy the specification merely because the shared module exists.

- **Strongest design element:** SQLite local completion cleanly separates verified bytes from caller finalization and
  makes `FinalizationIncomplete` truthful and recoverable.
- **Most likely wrong assumption:** The latest terminal row per caller may prove insufficient if a caller later permits
  out-of-order operations. Current Branch and Watch ordering forbids that behavior.
- **Highest-risk dependency:** Existing Watch and Branch code combines policy, mutation, tests, and mutable process state
  in large files. Migration can accidentally move caller policy into the shared module.
- **Easiest way to overbuild:** Add a durable running-operation ledger, generalized recovery engine, or public plan
  format.
- **Easiest way to under-test:** Mock filesystem and SQLite behavior or prove only final hashes without failures between
  mutation, commit, marker cleanup, and finalization.
- **Simpler alternative considered:** Keep only the current serialization wrapper and align callers informally. It was
  rejected because it provides no leverage over the ordering and failure semantics this work exists to centralize.
- **Residual Product V1 risk:** Abrupt termination can leave a prepared zip or a partially updated working directory
  that matches no known root. Grace preserves evidence and requires explicit user action rather than guessing.

This specification is ready for `dev-process` spec-to-plan compilation. It does not create issues, branches, or
implementation work.
