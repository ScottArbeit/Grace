# Grace Cache Implementation Audit

## Purpose and status vocabulary

This is the canonical Product V1 completion audit for Grace Cache Epic #597. It records what current source and
proof already establish, what the approved recovery plan still requires, and which work is intentionally absent. It is
not a historical addendum and does not turn an old issue, branch, or pull request into a current requirement.

- `implemented and proven`: current implementation and focused proof are recorded.
- `planned`: accepted Product V1 work without an implementation claim.
- `blocked`: accepted work that cannot begin until its named dependency is complete.
- `deferred`: intentionally not a Product V1 outcome.
- `out of scope`: intentionally absent from Product V1.
- `superseded`: older work that must not be resumed as the implementation path.

Every final-audit row must cite its implementation seam, proof seam, current issue or pull request evidence, and any
remaining risk. Checkbox state alone is not evidence.

## Product V1 baseline

Product V1 uses one Linux x64 cache host under systemd and a system-managed account, with an OS-protected private
cache/key directory. A cache has one static P-256 service identity until manual revocation and re-enrollment. Grace
does not export that private key and makes no hardware-backed custody promise.

The included user value is a server-resolved immutable full-root plan, Direct, CachePreferred, and CacheRequired mode
behavior, static enrollment and truthful status, bounded registration liveness, validated local storage, authorized
read-through, and the full-root miss-fill-serve-hit tracer. Each cache request remains subject to its current grant and
holder proof; cached bytes alone are never permission to serve an artifact.

Automatic or startup cache identity rotation, candidate promotion, prefetch, scheduled retention or eviction, Watch
integration, Grace.Operations integration, broad platform parity, HA/DR, and hostile-root defense are not Product V1
outcomes. Correctness cleanup for incomplete staging or failed commits remains part of the included store behavior.

## Current tracker and dependency state

| Evidence | Current state | Audit use |
| --- | --- | --- |
| [Owner decision for #597](https://github.com/ScottArbeit/Grace/issues/597#issuecomment-5263063211) | Product V1 is approved as the governing contract. The final `epic/597` to `main` pull request still needs explicit maintainer approval at its reviewed, validated current head. | Final release gate. |
| [Epic #597](https://github.com/ScottArbeit/Grace/issues/597) | The approved decision governs over older rotation text. | Scope source. |
| [Mini-epic #601](https://github.com/ScottArbeit/Grace/issues/601) | Open parent for replacement runtime and store work. | Owns the replacement leaf sequence. |
| [PR #723](https://github.com/ScottArbeit/Grace/pull/723) | Closed as superseded without merge at `647f4067252e5f2805e76a492d26096a854a75a9`. | Selectively inspect independent work; do not merge or replay it wholesale. |
| [Issues #622](https://github.com/ScottArbeit/Grace/issues/622) and [#724](https://github.com/ScottArbeit/Grace/issues/724) | Closed and not planned. | Do not resume as cache work. |
| [Issue #855](https://github.com/ScottArbeit/Grace/issues/855) | Complete R0 static-contract pruning. | Required predecessor completed. |
| [Issue #856](https://github.com/ScottArbeit/Grace/issues/856) | Superseded by the narrower #886 and #887 R1 sequence. | Do not resume its mixed server, command, and status scope. |
| [Issue #886](https://github.com/ScottArbeit/Grace/issues/886) and [PR #888](https://github.com/ScottArbeit/Grace/pull/888) | R1A static enrollment identity merged to the integration branch. | Owns only static enrollment state, protected local identity, and their focused proof. |
| [Issue #887](https://github.com/ScottArbeit/Grace/issues/887) and [PR #896](https://github.com/ScottArbeit/Grace/pull/896) | R1B repository-independent enrollment and status commands are implemented in the current pull request. | Consumes R1A without owning protected-identity implementation detail. |
| [Issue #857](https://github.com/ScottArbeit/Grace/issues/857) | Open R2: bounded registration liveness. | Starts after R1 and its liveness research gate. |
| [Issue #835](https://github.com/ScottArbeit/Grace/issues/835) | Open. Later local materialization is blocked until it merges to `main` and Epic #597 is refreshed. | Required sequence for #628 to #630 and #634. |

## Implemented and proven server foundations

### Server-resolved materialization plans and execution modes

- **Implementation seam:** `src/Grace.Server/Materialization.Server.fs` resolves the target root and creates Direct,
  CachePreferred, or CacheRequired plan shapes. Root artifact validation requires the DirectoryVersion zip and recursive
  metadata artifacts. CacheRequired availability uses the existing `cacheRequiredUnavailable` error contract.
- **Proof seam:** `src/Grace.Server.Unit.Tests/MaterializationPlan.Server.Tests.fs` and
  `src/Grace.Types.Tests/MaterializationPlan.Types.Tests.fs` cover the public plan and mode contract.
- **Status classification:** `implemented and proven` for server plan resolution and source selection. This does not
  claim a cache-host fetch, store, or local materialization implementation.
- **Residual risk:** The cache tracer must extend these server foundations through an actual authorized cache request.

### Artifact grants and holder proofs

- **Implementation seam:** `Grace.Types.ArtifactGrant`, `Grace.Shared.ArtifactGrant`,
  `src/Grace.Actors/ArtifactGrantSigningKey.Actor.fs`, and
  `src/Grace.Server.Security/ArtifactGrantKeys.Server.fs` define and publish the signed artifact-grant contract. Grants
  bind the requester, selected cache, immutable target root, execution mode, and artifact identity; request proofs bind
  the grant to the exact method, route, and artifact.
- **Proof seam:** Artifact-grant validation, request-proof validation, signing-key actor, and integration tests cover
  valid and rejected grants, holder mismatch, binding mismatch, expiry, overlap validation keys, and unknown-key
  fail-closed behavior. #619 and PR #697 recorded the focused proof and generated-contract evidence.
- **Status classification:** `implemented and proven`.
- **Residual risk:** Artifact-grant validation-key rollover remains an existing server capability. It is separate from
  cache service identity and must survive R0 pruning.

### Server cache-registration foundation

- **Implementation seam:** `src/Grace.Actors/CacheRegistration.Actor.fs` and
  `src/Grace.Server/CacheRegistration.Server.fs` provide administrator-controlled enrollment, refresh, revocation,
  assignment, durable registration state, and proof verification. Selection is limited to an eligible current
  registration with its explicit repository assignments.
- **Proof seam:** `CacheRegistrationLifecycleTests` and registration type tests cover enrollment facts, refresh,
  revocation, repository selection, malformed and duplicate inputs, durable state, and proof verification. #600 and
  PR #706 recorded the server-foundation validation.
- **Status classification:** `implemented and proven` for the server foundation.
- **Residual risk:** The static server foundation still requires the separate R1A identity and R2 liveness leaves before
  a cache can safely participate in the later runtime path. Artifact-grant validation-key rollover remains separate.

### Direct materialization

- **Implementation seam:** `grace connect` consumes a Direct plan, validates the selected root artifacts, stages
  content, and publishes local state only after validation succeeds.
- **Proof seam:** `Grace.CLI.Tests.ConnectTests` covers Direct plan shape, root-consistency rejection, integrity,
  staged extraction, retry behavior, and byte equivalence.
- **Status classification:** `implemented and proven` for Direct materialization.
- **Residual risk:** This does not establish CachePreferred or CacheRequired host execution.

## Accepted Product V1 work

### R0: static contract pruning (#855)

- **Status classification:** `implemented and proven`.
- **Recorded result:** #855 removed cache service-identity rotation and candidate surfaces while retaining the separate
  artifact-grant validation-key rollover behavior.
- **Recorded proof:** #855 records its inventory, generated-contract freshness, and focused grant-key validation proof;
  the current source has no active cache service-identity rotation surface.

### R1A: static enrollment identity foundation (#886)

- **Status classification:** `implementation leaf`.
- **Required result:** Enrollment has no caller `Health`; the server creates an `Unhealthy` durable registration before
  success, and existing selection excludes it. The internal Linux-only identity primitive stages one `0700` attempt
  directory with a flushed `0600` PKCS#8 P-256 key, then publishes `0700` ready only after a flushed `0600`
  registration configuration matches the derived base64url SHA-256 `X || Y` fingerprint.
- **Proof:** Actor persistence failure cannot advance authoritative in-memory selection; raw JSON `Health` cannot make
  a new registration healthy; inspection distinguishes missing, attempt, ready, invalid, and inaccessible without
  mutation; Linux tests restore modified modes before cleanup.
- **Deferred:** #887 owns HTTP, credentials, command output, accepted/rejected/unknown orchestration, and cleanup calls;
  #857 owns signed refreshes that remain `Unhealthy`; #625 publishes `Healthy` only after serving readiness is proven.
  R1A adds neither serving, rotation, reconciliation, non-Linux support, nor CacheStore behavior.

### R1B: repository-independent enrollment and status commands (#887)

- **Status classification:** `implementation leaf`.
- **Required result:** `grace cache enroll` validates local inputs and state, resolves one existing bearer before key
  staging, sends one selected-server request, and publishes ready only after the accepted configuration commits.
  `grace cache status` reads local state only and returns a redacted result with an enrolled exit code only for a valid
  ready identity.
- **Propagation:** CLI root dispatch bypasses repository configuration and invocation history; the narrow SDK facade
  accepts the selected URI and resolved bearer directly. Server routes, DTOs, OpenAPI, generated clients, cache host,
  liveness, storage, retries, and reconciliation are unchanged.
- **Proof:** The focused Linux CLI root-command suite passes the registered Cache cases: parser registration, inert help/schema/examples,
  complete-buffer JSON parsing, pure repository-independent status, ready/weak/corrupt redaction and exit codes, PAT
  success/rejection/invalid input, M2M success/acquisition failure, missing and expired interactive results,
  cancellation before and after staging, malformed nominal success, and forced local-commit failure. The bounded
  internal test dependencies default to the production credential resolver and ready commit, then exercise controlled
  outcomes through actual root command dispatch. The suite records exact M2M token and enrollment request counts, PAT
  bearer forwarding, protected ready publication, failure cleanup, redacted JSON, and no repository `.grace` state. A
  separate unprivileged Linux executable run observes inaccessible state as redacted `invalid`/`inaccessible` JSON with
  exit code `1`.
- **Interactive proof composition:** The Linux secure-store harness provisions an ephemeral D-Bus session, GNOME
  Keyring, libsecret, and unlocked collection. It drives normal device login and production Cache root dispatch for a
  valid stored bearer and an expired stored bearer: 2/2 pass. Existing Auth tests continue to cover producer selection
  and invalid token behavior.

### R2: registration liveness (#857)

- **Status classification:** `planned`.
- **Required result:** One cache process uses one bounded scheduler with server-issued liveness times, signed refreshes
  that remain `Unhealthy`, bounded retry, and terminal fail-closed behavior at expiry, revocation, or definitive
  rejection. #625, not #857, publishes `Healthy` after serving readiness is proven.
- **Gate:** Before implementation, record the current registration response classes, timestamps, selection behavior,
  clock model, retry schedule, and expiry cap.
- **Proof:** Startup, endpoint validation, guard conflict, refresh, temporary retry, capped `Retry-After`, revocation,
  expiry, shutdown race, restart, and server selection with an injectable clock.

### Artifact store, read-through, and tracer

- **Status classification:** `planned`.
- **Required result:** A selected cache accepts only a current authorized request for the exact full-root plan. On a
  miss, it fetches the exact server-selected artifacts, validates hash and size, atomically commits a complete set,
  serves the bytes, and returns a hit for the same authorized artifact on the next request.
- **Proof:** The tracer must demonstrate `miss -> fill -> serve -> hit`, plus rejected grant, holder proof, route,
  artifact, hash, size, partial-staging, incomplete-store, stale-registration, and CacheRequired no-fallback cases.
- **Residual risk:** Local bytes do not prove entitlement, completeness, or current liveness.

### Later local materialization

- **Status classification:** `blocked`.
- **Required result:** Cache-aware local execution prepares exact validated content and calls
  `WorkingDirectoryUpdate.run`; it does not create a second working-tree, SQLite, Branch, Watch, or recovery path.
- **Dependency:** Do not begin #628 to #630 or #634 until #835 merges to `main` and the Epic #597 branch is refreshed.

## Deferred and out-of-scope work

| Capability | Status classification | Product V1 disposition |
| --- | --- | --- |
| Automatic or startup cache identity rotation, candidate promotion, and rotation recovery | deferred | No active lifecycle, route, setting, timer, or retry surface remains after R0. |
| Prefetch | deferred | Read-through is the supported acquisition path. |
| Scheduled retention or eviction | deferred | Only correctness cleanup for incomplete staging or failed commits is included. |
| Watch integration | deferred | No cache-facing Watch contract is required for Product V1. |
| Grace.Operations integration | deferred | No Operations contract is required for Product V1. |
| Platform parity | out of scope | The selected Linux x64 deployment profile is the only advertised host profile. |
| HA/DR | out of scope | No high-availability or disaster-recovery outcome is promised. |
| Hostile-root defense | out of scope | Product V1 uses the stated OS-protected local deployment boundary. |

## Contract propagation and final gates

Every accepted change must classify the following surfaces as updated, unchanged with reason, deferred to a named
issue, or not applicable: shared types, persisted state/events, server routes and error envelopes, CLI, SDK, OpenAPI
and generated clients, cache host/configuration, storage, diagnostics, tests, and documentation. No accepted field,
route, state, command, or setting may remain half-active.

The final Epic #597 release candidate requires all of the following:

1. Each accepted audit row has current-head implementation and proof evidence.
2. The authorized full-root miss-fill-serve-hit tracer passes.
3. #835 sequencing is honored for later local materialization.
4. Generated-contract and documentation checks pass where a public surface changed.
5. Current-head validation and a fresh review complete for every relevant pull request.
6. The final `epic/597` to `main` pull request receives explicit maintainer approval at that reviewed, validated head.

## R1 validation status

The R1A source, contract, generated-artifact, and focused-proof changes merged through PR #888. The R1B command and
output work is implemented in PR #896. Required evidence is
targeted F# formatting, affected Release builds and tests, OpenAPI/generated freshness, MarkdownLint, `git diff --check`,
and a passing current-head GitHub `Validate` run that executes the Linux permission and inaccessible-state cases.
Windows focused identity proof skips those Linux-only cases, so it cannot replace hosted Linux evidence.

The following documentation checks remain part of that focused proof:

```powershell
npx --yes markdownlint-cli2 "docs/Grace Cache.md" "docs/Grace Cache implementation audit.md"
git diff --check
```

This status claims the R1B enrollment and status commands only. It excludes R2 liveness, serving, rotation,
reconciliation, non-Linux support, and CacheStore behavior.
