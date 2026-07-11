# Grace Cache Implementation Audit

Issue #609 creates this scaffold for the Grace Cache materialization epic. The scaffold records
requirement classification, proof seams, and residual risk as implementation PRs land. It is not a
second task tracker.

GitHub issues and pull requests remain the active tracker and the source of implementation truth.
Update this document only when the related issue or PR can cite current evidence. Do not use this
document to claim completion without an owning issue, PR, commit, or validation result.

## Scope

- Parent epic: #597, Grace Cache and Server-Resolved Materialization Plans.
- Governance mini-epic: #598, Grace Cache governance, docs, and review-prevention.
- Scaffold issue: #609, GC-02.
- Branch family: `epic/598-grace-cache-governance-docs-review-prevention` and its leaf PRs.

This document audits Grace Cache implementation evidence only. It does not authorize broad support
claims, create follow-up buckets, or replace the dependency graph in GitHub.

## Status Vocabulary

Use exactly one of these status classifications for each audit entry:

- `implemented and proven`: the implementation exists and current proof evidence is recorded.
- `implemented but proof incomplete`: the implementation exists, but proof is partial, stale, or
  intentionally deferred to a named issue or PR gate.
- `deferred`: the requirement is real, but it is blocked by a named dependency or future issue.
- `out of scope`: the requirement is outside the accepted scope for this epic or milestone.
- `rejected`: the requirement or implementation shape is intentionally not accepted.
- `not applicable`: the requirement does not apply to this surface, with rationale.

Do not use checkbox state as the status. If an entry changes status, cite the issue or PR that
changed it and the evidence that makes the classification current.

## Required Entry Fields

Every audit entry must keep these fields:

- Implementation seam: the component, route, command, contract, actor, storage shape, doc, or
  generated artifact that owns the behavior.
- Proof seam: the test, validation command, static proof, generated freshness check, manual audit,
  or external dependency that proves the seam.
- Status classification: one status from the vocabulary above.
- Issue or PR evidence: GitHub issue, pull request, commit, or validation artifact that owns the
  current claim.
- Residual risk or rationale: remaining risk, blocked dependency, rejected shape, or reason the
  entry is not applicable.

## Final Audit Categories

The final epic audit should classify these categories before the release-candidate PR is treated as
review-ready:

- User-selected materialization mode and fallback behavior.
- Server-resolved content scope and RecursiveDirectoryVersions authority.
- ContentAccessGrant issuance, binding, validation, and expiry.
- Grace Cache service registration and identity.
- Read-through behavior and cache-hit enforcement.
- Prefetch behavior and retention refresh.
- Cache artifact metadata, cleanup, and artifact completeness.
- Watch dependency impact for #473 and #552.
- Operations dependency impact for #554.
- Public docs and generated contracts.
- Validation evidence and residual risks.

## Grace Cache Artifact Authorization Guardrails

Future implementation PRs must preserve these V1 guardrails when updating any audit row:

- V1 cache artifacts are Materialization Plan artifacts. Cache hits depend on artifact completeness
  and current artifact grants, not low-level storage or object-placement proof.
- Full-root artifacts require current whole-target-root authorization for the plan artifact. Matching
  immutable target identity is necessary, but it is not an authorization scope by itself.
- Narrowed path-scope grants must be rejected for V1 full-root artifacts until Grace accepts a
  path-scoped artifact shape and proof obligation.
- Cache Service Identity authorizes configured cache operations only. It must not become a global
  artifact reader or bypass per-call artifact grant validation.

## Requirement Group Scaffold

Each group starts as a scaffold entry for #609. Future implementation PRs should replace the
docs-only classification with current evidence for the behavior they own.

### Direct Materialization

- Implementation seam: `grace connect` requests `MaterializationExecutionMode.Direct` with
  `MaterializationCacheSelection.Bypass`, downloads the planned DirectUri recursive metadata and
  DirectoryVersion zip artifacts, validates descriptor/root/integrity evidence, stages extraction,
  then writes the working tree, local status, and object-cache rows only after validation passes.
- Proof seam: `Grace.CLI.Tests.ConnectTests` covers Direct plan shape, root consistency rejection,
  delivered-artifact integrity, staged extraction before local state writes, retry-once behavior for
  retryable artifact-source failures, no retry for permanent root mismatch, and byte equality for
  manifest-backed plus whole-file materialization.
- Status classification: `implemented and proven`.
- Issue or PR evidence: #616 adds the Direct retry, negative consistency, manifest-backed byte
  equivalence, object-cache regression, and `pwsh ./scripts/validate.ps1 -Fast` evidence for the
  tracer branch.
- Residual risk or rationale: OpenAPI/static contract propagation for the previous Runtime
  `ReferenceType` decision is deferred to #687; this row only claims the Direct tracer behavior and
  does not claim CachePreferred or CacheRequired support.

### CachePreferred Materialization

- Implementation seam: mode selection and materialization path that tries Grace Cache first and can
  fall back to Direct only when the accepted contract allows it.
- Proof seam: positive and negative tests for cache hit, cache miss, grant failure, cache outage,
  and fallback observability.
- Status classification: `implemented and proven` for server-side plan selection and source shape.
- Issue or PR evidence: #620 owns current-registration selection, ordinary-absence Direct fallback,
  holder-bound grant issuance, complete-plan validation, generated contracts, and Fast validation.
- Residual risk or rationale: #629 and #630 still own client private-key lifetime and cache execution.

### CacheRequired Materialization

- Implementation seam: mode selection and materialization path that fails closed when Grace Cache
  cannot serve the authorized content.
- Proof seam: tests proving no Direct source is used when CacheRequired cannot satisfy the request,
  including cache miss, stale grant, malformed grant, and unavailable cache cases.
- Status classification: `implemented and proven` for server-side plan selection and source shape.
- Issue or PR evidence: #620 proves selected-cache plans and retryable failure on ordinary absence,
  selection failure, or post-selection grant failure without Direct retrieval details.
- Residual risk or rationale: CacheRequired is a high-risk trust contract because a silent Direct
  fallback would violate the mode name and caller expectations.

### Server-Side Plan Resolution And Recursive Metadata Authority

- Implementation seam: Grace Server materialization plan resolution for Reference and
  DirectoryVersion targets, including the resolved DirectoryVersionId that owns target-root
  artifacts and RecursiveDirectoryVersions metadata.
- Proof seam: server-surface tests or static proof showing Grace Server resolves every Reference or
  DirectoryVersion target before cache interaction, emits recursive metadata for the same resolved
  DirectoryVersionId, and rejects cache or client code as a resolution authority.
- Status classification: `not applicable` to this docs-only scaffold.
- Issue or PR evidence: #609 creates the audit slot; the owning server-resolution issue or PR must
  cite implementation and proof before claiming server-resolved content scope or
  RecursiveDirectoryVersions authority.
- Residual risk or rationale: later cache, SDK, or CLI rows cannot satisfy this authority seam by
  resolving Reference or DirectoryVersion inputs locally. Grace Server must remain the source of the
  plan, resolved DirectoryVersionId, and recursive metadata used by cache artifacts.

### ContentAccessGrant Issuance And Validation

- Implementation seam: #619 defines `Grace.Types.ArtifactGrant`, `Grace.Shared.ArtifactGrant`,
  `Grace.Actors.ArtifactGrantSigningKeyActor`, and `Grace.Server.Security.ArtifactGrantKeys` as the
  signed artifact grant contract. One fixed-key Orleans actor backed by `GraceActorStorage` persists
  P-256 private keys before use and owns rotation for every Grace Server instance. Grants bind the
  authenticated `grace_user_id` as a `User` requester, the canonical ephemeral holder-key
  thumbprint, selected Cache service principal, immutable target root, non-Direct execution mode,
  and explicit artifact identities. Each cache artifact request carries a stateless holder proof
  over the grant digest, normalized method and route, artifact identity, and a presentation window
  of at most 30 seconds with at most 30 seconds of clock-skew tolerance. Canonical request encoding
  uppercases and trims the HTTP method; it trims the route, excludes query and fragment text, adds a
  leading slash when absent, and otherwise preserves path case and trailing-slash meaning.
- Lifecycle seam: the default grant TTL is 5 minutes, the maximum accepted grant TTL is 15 minutes,
  signing keys are active for 2 hours, old validation keys remain published through the final grant
  overlap window, and `/cache/validation-keys` advertises a 15-minute cache TTL. Grant and proof
  expiry are checked when a request is admitted; #625 owns allowing an admitted response to stream
  to completion and requiring fresh proof for every retry, resume, range, parallel, or later request.
- Proof seam: `ArtifactGrantValidationTests` covers valid grants, Direct mode skipping grant
  validation, unsigned grants, missing key ids, unsupported algorithms, wrong cache, wrong target
  root, wrong execution mode, wrong artifact, wrong signatures, expired grants, overlong TTLs,
  expired keys, current/overlap key validation, and one-attempt unknown-key refresh fail-closed
  behavior. `ArtifactRequestProofValidationTests` covers matching holder proof, copied-grant use with
  another key, exact grant/method/route/artifact binding, tampering, proof lifetime and skew
  boundaries, and explicit Direct bypass. `ArtifactGrantSigningKeyActorTests` covers
  persist-before-use, activation recovery, storage-write failure without local fallback, 2-hour
  rotation, overlap retirement, concurrent signing/publication, and malformed issuance boundaries.
  `ArtifactGrantKeysIntegrationTests` proves the HTTP publication route resolves stable actor-backed
  keys through the Aspire-hosted server.
- Status classification: `implemented and proven`.
- Issue or PR evidence: #619 and PR #697 own the grant/key contract, validation-key publication
  route, generated OpenAPI/SDK artifacts, focused proof, and passing `validate.ps1 -Full` evidence.
- Residual risk or rationale: #620 derives `RequesterPrincipalId` from authenticated server context
  and adds the holder public key and grant to cache-mode Materialization Plans. #625 owns request
  admission and stream behavior; #629 and #630 own ephemeral private-key lifetime
  and just-in-time proof generation. V1 intentionally has no nonce store, so replay of a captured
  complete request remains possible inside the narrow proof window; TLS protects it in transit.

### Cache Service Registration And Identity

- Implementation seam: #617 defines the pre-registration identity boundary in
  `Grace.Server.Security.CacheServiceIdentity`. #618 adds
  `Grace.Types.CacheRegistration`, `CacheRegistrationActor`, and the `/cache/register` plus
  `/cache/refresh` server routes. Registration state is server-owned, keyed by service principal,
  records the cache endpoint, approved scopes, approved capabilities, approved execution modes,
  read-through and prefetch flags, a 2-hour active lifetime, and a 1-hour refresh-after interval.
- Proof seam: `CacheServiceIdentityUnitTests` covers disabled configuration, fail-fast enabled
  configuration, valid OIDC service-principal registration, PAT rejection, normal user rejection,
  missing client-credentials marker rejection, unapproved scope and capability rejection, and
  non-secret status summaries. `CacheRegistrationLifecycleTests` covers default lifetime,
  duplicate register upsert behavior, refresh-before-due behavior, refresh-after-due extension,
  expired registration exclusion, selection query filtering, and HTTPS endpoint validation.
- Status classification: `implemented but proof incomplete`.
- Issue or PR evidence: #617 owns the service credential, identity claim, allowed scheme, scope
  model, configuration boundary, docs, and `pwsh ./scripts/validate.ps1 -Fast` validation for the
  preflight. #618 owns the registration actor/API implementation and must cite its PR, commit, and
  final validation evidence before this row can move to `implemented and proven`.
- Residual risk or rationale: #619 still owns per-call artifact grants. #620 still owns cache-mode
  plan generation and consuming the eligible-registration selection seam. Cache Service Identity and
  Cache registration authorize configured registration behavior only; they must not become global
  artifact readers or bypass per-call artifact grants.

### Read-Through Behavior

- Implementation seam: Grace Cache request handling that serves only complete Materialization Plan
  artifacts after a current grant authorizes the plan's full target-root scope.
- Proof seam: tests for authorized complete-plan hit, unauthorized hit, stale artifact metadata,
  partial artifact presence, narrowed path-scope grant rejection for full-root artifacts, and
  server-authority refresh where the contract requires it.
- Status classification: `not applicable` to this docs-only scaffold.
- Issue or PR evidence: #609 creates the audit slot; read-through work must add current proof before
  claiming cache reads.
- Residual risk or rationale: local artifact presence is not permission, liveness, or proof that a
  caller may fetch a full target-root materialization.

### Prefetch Behavior

- Implementation seam: configured prefetch subscriptions, Materialization Plan resolution, artifact
  fetch scheduling, artifact metadata writes, retention refresh, grant enforcement, and failure
  reporting.
- Proof seam: tests for authorized full-root prefetch, denied prefetch, duplicate subscription,
  partial artifact fetch, retry, cancellation, artifact completeness checks, and retention refresh
  without creating separate retention classes.
- Status classification: `not applicable` to this docs-only scaffold.
- Issue or PR evidence: #609 creates the audit slot; prefetch work must cite the owning issue or PR.
- Residual risk or rationale: prefetch can place plan artifacts in cache before a requester needs
  them, but serving still requires per-call authorization for the artifact scope.

### Cache Metadata And Cleanup

- Implementation seam: Cache Artifact Metadata, Cache Retention, artifact completeness state, expiry
  decisions, and cleanup side effects for target-root zips plus recursive metadata.
- Proof seam: tests for metadata creation, retention refresh, expiry, cleanup after partial artifact
  failure, shared artifact retention, and no pinning in v1.
- Status classification: `not applicable` to this docs-only scaffold.
- Issue or PR evidence: #609 creates the audit slot; cleanup work must cite its owning issue or PR.
- Residual risk or rationale: cache cleanup must not infer authorization, artifact completeness, or
  global content liveness from local artifact presence alone.

### Watch Dependencies

- Implementation seam: Watch integration points that consume or publish cache-related materialization
  status after #473 and #552 establish their required boundaries.
- Proof seam: dependency evidence from #473 and #552 plus focused tests that prove Watch does not use
  stale materialization mode, grant, reference, or cache-status snapshots.
- Status classification: `deferred`.
- Issue or PR evidence: dependency-gated by #473 and #552; #609 records the audit slot only.
- Residual risk or rationale: Watch impact must not silently disappear. Do not mark this entry
  implemented until current Watch dependency evidence exists.

### Operations Dependencies

- Implementation seam: Operations surfaces that observe, configure, validate, or report Grace Cache
  materialization behavior after the #554 Operations branch defines the relevant contracts.
- Proof seam: dependency evidence from #554 plus Operations-local validation for any exposed command,
  worker, report, or status surface.
- Status classification: `deferred`.
- Issue or PR evidence: dependency-gated by #554; #609 records the audit slot only.
- Residual risk or rationale: Operations support is not claimed by core cache docs alone. Any
  Operations impact needs current #554 evidence before completion language is allowed.

### Public Documentation

- Implementation seam: user, operator, contributor, and architecture docs that describe Grace Cache
  modes, grants, registration, prefetch, read-through, cleanup, and dependency limits.
- Proof seam: MarkdownLint, manual rendered review, link checks when practical, and comparison
  against the accepted product spec and implementation issues.
- Status classification: `not applicable` to this docs-only scaffold.
- Issue or PR evidence: #609 creates this audit scaffold; later docs PRs must cite their owning
  issues and validation.
- Residual risk or rationale: docs must distinguish proven support, incomplete proof, deferrals, and
  non-claims instead of using broad supported wording.

### Generated Contracts

- Implementation seam: OpenAPI, SDK generated clients, generated metadata, CLI machine-readable
  output, or other generated artifacts that expose Grace Cache requests, statuses, grants, or modes.
- Proof seam: generated freshness checks, OpenAPI proof checks, SDK generation checks, contract
  tests, and static scans for stale schema or enum values.
- Status classification: `not applicable` to this docs-only scaffold.
- Issue or PR evidence: #609 creates the audit slot; generated-contract work must cite the issue or
  PR that updates artifacts and proof.
- Residual risk or rationale: generated drift is a release risk. Do not rely on source-only changes
  when public or SDK-visible cache contracts change.

## Final Audit Update Rules

- Update an entry only from current branch evidence, not from memory or older dependency snapshots.
- Preserve the exact status vocabulary so the final audit can be searched and compared mechanically.
- Replace the #609 scaffold evidence with the owning issue or PR evidence when implementation lands.
- Keep dependency-gated Watch and Operations entries until their named dependency evidence is current.
- Record rejected or out-of-scope decisions with rationale, not as anonymous follow-up work.
- Keep unproven support language narrow: name the seam, proof, and residual risk.

## Docs-Only Validation For This Scaffold

The #609 worker should validate this document with:

```powershell
npx --yes markdownlint-cli2 "docs/Grace Cache implementation audit.md"
git diff --check
```

Manual review must also confirm that the scaffold covers Direct, CachePreferred, CacheRequired,
server-side Reference/DirectoryVersion resolution, RecursiveDirectoryVersions metadata authority for
the same DirectoryVersionId, grants, registration, read-through, prefetch, cleanup, Watch,
Operations, docs, generated contracts, validation evidence, and residual risks.
