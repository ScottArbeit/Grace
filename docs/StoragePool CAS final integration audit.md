# StoragePool CAS Final Integration Audit

Issue #434 is the final audit slice for #424, the StoragePool-scoped content-addressable storage
satellite epic. This artifact records the evidence that the current `epic/424-storagepool-cas`
branch is ready for the orchestrator to open the final CAS integration pull request into
`epic/343-blake3-sha256-version-hashes`.

## Required Branch Flow

Issue #424 intentionally deviates from the normal Grace epic rule.

- #424 descends from `origin/epic/343-blake3-sha256-version-hashes`, not `origin/main`.
- This issue branch starts from `origin/epic/424-storagepool-cas`.
- The issue #434 pull request must target `epic/424-storagepool-cas`, not `main` and not
  `epic/343-blake3-sha256-version-hashes`.
- After #434 lands, the final CAS integration pull request targets
  `epic/343-blake3-sha256-version-hashes`.
- PR #396 remains the final Epic 343 release-candidate pull request to `main`; it must not be
  auto-merged by this slice.

Grace is still pre-production. There is no production data to import, migrate, preserve,
classify, grandfather, or read through old CAS formats or old object-key paths.

## Implementation Preflight

- Acceptance criteria to prove:
  - #425 through #433 are closed, merged into `epic/424-storagepool-cas`, and checked in the #424
    body.
  - The final audit matrix maps each #424 invariant to concrete current-branch evidence.
  - Public contracts, generated artifacts, SDK call sites, authorization, route drift, storage
    routing, and whole-file regressions are either proven current or explicitly waived.
  - The artifact names PR #396 handoff requirements without claiming that downstream work has
    already happened.
- Contract surfaces to update or waive:
  - Updated: this final audit artifact.
  - Updated: generated SDK harness metadata projection SHA values after freshness validation found
    stale metadata.
  - Unchanged by this slice: F# runtime, OpenAPI source, bundled OpenAPI, SDK facade code, CLI
    behavior, and runtime configuration.
- Existing tests and checks to run or extend:
  - Run static evidence checks over current branch paths.
  - Run OpenAPI proof and SDK freshness checks in check mode.
  - Run MarkdownLint for this authored Markdown file.
  - Run `git diff --check`.
  - No F# tests are added because this slice records evidence and does not change runtime behavior.
- Adversarial cases:
  - Stale branch, stale generated artifacts, or stale CI/bot evidence reused from an older head.
  - Address-only content-block authorization after `StoragePoolId` became part of the durable
    identity.
  - Route drift from pool A to pool B causing save, download, annotation, or cleanup to trust the
    current repository route instead of stored manifest/session evidence.
  - Cross-account shard routing silently falling back to the default account or shared key.
  - Whole-file save/download paths accidentally forced through manifest-specific StoragePool
    evidence.
- Global options or modes:
  - Manifest-backed large files use StoragePool-scoped CAS placement.
  - Whole-file content remains repository-scoped.
  - Managed identity is the pre-production cross-account CAS SAS path; shared-key cross-account
    signing fails closed.
- Validation plan:
  - MarkdownLint this file.
  - `pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending`.
  - `pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check`.
  - Static PowerShell evidence searches for stale paths and required StoragePool route fields.
  - `git diff --check`.
  - No final build/test gate because this branch edits only an authored audit document.
- Touched paths:
  - `docs/StoragePool CAS final integration audit.md`.
  - `sdk/generated/generator-report.json`.
  - `sdk/typescript/grace/src/internal/generated/grace-raw-client-metadata.ts`.
  - `sdk/python/grace-sdk/src/grace_sdk/_generated/grace_raw_client_metadata.py`.
  - `sdk/python/grace-sdk/src/grace_sdk/_generated/__init__.py`.
  - `sdk/rust/grace-sdk/src/generated.rs`.
  - `sdk/dotnet/Grace.Sdk.Harness/Internal/Generated/GraceRawClientMetadata.cs`.
  - The write set expanded only after `generate-sdk-clients.ps1 -Mode Check` reported stale
    generated metadata, and the correction is deterministic generated freshness output.

## Integration State Evidence

- Local branch head before this document was authored:
  `29df49b5af417f0e348e13d6e833cd4297a5151c`.
- `origin/epic/424-storagepool-cas` head before this document was authored:
  `29df49b5af417f0e348e13d6e833cd4297a5151c`.
- `git rev-list --left-right --count origin/epic/424-storagepool-cas...HEAD` returned `0 0`.
- `git rev-list --left-right --count origin/epic/343-blake3-sha256-version-hashes...origin/epic/424-storagepool-cas`
  returned `77 265`; this is expected before the final CAS integration pull request because #424
  intentionally carries the CAS satellite work ahead of the #343 branch.
- Native GitHub sub-issue verification for #424 returned `totalCount: 10`.
- Native sub-issue state:
  - #425 closed.
  - #426 closed.
  - #427 closed.
  - #428 closed.
  - #429 closed.
  - #430 closed.
  - #431 closed.
  - #432 closed.
  - #433 closed.
  - #434 open.
- #424 body checklist has #425 through #433 checked and #434 unchecked.

## Child Pull Request Evidence

- #425 merged by PR #435 at `5412e933ba4d35ebca76a4eda7a0967f277b4fd1`.
- #426 was completed through the CAS-03 mini-epic integration PR #453 at
  `647bbc84ec89085fba036e2040cb8db9fbdd66fc`; the earlier PR #436 is closed and superseded.
- #427 merged by PR #454 at `411af75f8b76d565abe46725d3f0109303114323`.
- #428 merged by PR #455 at `0944fba7744e3f9ad1660d8b6b91adc05130a311`.
- #429 merged by PR #456 at `9b2cbb1c57fbf0e9a9e1f7dae471b3aef67f7dbc`.
- #430 merged by PR #457 at `faa0f935e09a989c8d72d743ca7748aa96633c86`.
- #431 merged by PR #458 at `270b3a674ec80897483657a74e7f896fbc646f37`.
- #432 merged by PR #459 at `513fefa85ce2f83cb014c3729c9ed105d59cb377`.
- #433 merged by PR #460 at `29df49b5af417f0e348e13d6e833cd4297a5151c`.

## Final Audit Matrix

### Durable Route Evidence

Status: implemented and proven by current branch evidence.

- `ContentBlockMetadataActor` is keyed by `StoragePoolId|ContentBlockAddress` in
  `src/Grace.Actors/ContentBlockMetadata.Actor.fs`.
- `DirectoryVersion.Actor.fs` validates `FileManifest.StoragePoolId` before save, deduplicates
  manifest evidence by `(StoragePoolId, ManifestAddress, RelativePath)`, and checks content-block
  metadata through the stored manifest pool.
- `ManifestContributionWorkflow.Actor.fs`, `Reference.Actor.fs`,
  `RepositoryContentCounter.Actor.Tests.fs`, and `SaveBoundary.Actor.Tests.fs` carry
  `RepositoryId`, `StoragePoolId`, and `ManifestAddress` across ownership and cleanup boundaries.
- Route drift proof exists in
  `SaveBoundary.Actor.Tests.fs::ManifestSaveBoundaryResolverUsesStoredManifestPoolAfterRouteDrift`
  and
  `Storage.Server.Tests.fs::LargeBinarySaveLifecycleRequiresFinalizedManifestAndDownloadsAfterRouteChange`.

### Identity Keys

Status: implemented and proven by current branch evidence.

- ContentBlock metadata keys include the storage pool before the address.
- Manifest contribution workflow primary keys combine repository, storage pool, and manifest.
- Repository content counter tests distinguish the same manifest address across storage pools.
- Save boundary tests deduplicate recursive manifest references by stored pool and manifest
  address.
- Dedupe index tests require same-pool reuse and reject authoritative metadata from a different
  storage pool.

### Shared CAS Read Authorization

Status: implemented and proven by current branch evidence.

- `/storage/getContentBlockDownloadUri` is protected by `PathRead` over `AuthorizedScope` in
  `EndpointAuthorizationManifest.Server.fs` and `StorageAuthorizationResources.Server.fs`.
- `Storage.Server.fs` validates `AuthorizedScope`, `StoragePoolId`, `ManifestAddress`, and
  `ContentBlockAddress` before resolving finalized scoped content-block metadata.
- `Storage.Server.Tests.fs::ContentBlockDownloadUriRejectsAddressOnlyRequestBeforeSharedCasPlacementLookup`
  proves address-only reads fail before shared CAS placement lookup.
- `ManifestDownload.SDK.Tests.fs::ManifestDownloadRequestsOnlyManifestIdentityForEachBlockUri`
  proves SDK downloads request per-block URIs by manifest identity, not by reposting a full
  manifest or using address alone.

### Route Drift

Status: implemented and proven by current branch evidence.

- Save boundary tests use stored manifest pool evidence after route drift.
- Storage integration tests cover a large binary save lifecycle that downloads after route change.
- Normal file and annotation materialization tests reject wrong-pool metadata and missing finalized
  placement evidence.
- PR #436 review-loop regressions are represented in later tests for finalized upload metadata,
  replay hydration, route drift, claim validation, and final CAS materialization ordering.

### Public Contracts And Generated Artifacts

Status: implemented and ready for freshness validation in this slice.

- OpenAPI source files include `StoragePoolId`, `ManifestAddress`, `AuthorizedScope`,
  `ContentBlockStoragePlacement`, and server-selected storage placement evidence in
  `src/OpenAPI/Storage.Components.OpenAPI.yaml` and `src/OpenAPI/Storage.Paths.OpenAPI.yaml`.
- Bundled OpenAPI artifacts include the same fields in `src/OpenAPI/Grace.OpenAPI.yaml` and
  `src/OpenAPI/Grace.OpenAPI.3.1.2.yaml`.
- `src/OpenAPI/OpenAPI.ProofManifest.json` records source and generated artifact digests for the
  bundled OpenAPI outputs.
- Generated TypeScript, Python, and Rust matrix outputs include storage placement and manifest
  models under `sdk/generated/matrix/openapi-generator/**`.
- `sdk/generated/matrix/generator-matrix-evidence.json` records OpenAPI Generator TypeScript,
  Python, and Rust as accepted raw-client proof behind facades.
- `sdk/generated/generator-report.json` and generated SDK harness metadata now record
  `f3302412cae350c013786e3a95a87058f0337fabddff422385fee2135a5cd00e`, the current
  `Grace.OpenAPI.3.1.2.yaml` projection SHA from the OpenAPI proof manifest.
- `docs/StoragePool routing contract audit.md` records the #432 contract audit and the generated
  freshness plan.

### Storage Account, Container, And Credentials

Status: implemented with pre-production managed identity waiver.

- `Storage.SDK.Tests.fs` covers custom endpoints, CNAME endpoints, private-link endpoints, IP
  endpoints, Azurite path-style parsing, and shard evidence fragments.
- `Storage.Server.Tests.fs::ContentBlockPlacementParserUsesShardFragmentForIpHostCustomEndpoint`
  and `ContentBlockPlacementParserKeepsAzuritePathStyleWhenFragmentNamesSameAccount` cover server
  placement parsing boundaries.
- `Services.Actor.fs` fails closed when a routed shard account cannot be signed with the configured
  shared-key account and directs cross-account CAS SAS to managed identity.
- Managed identity support is sufficient for pre-production handoff. Production operational hardening
  remains outside this CAS child issue.

### Whole-File Regression

Status: implemented and proven by current branch evidence.

- Whole-file content remains repository-scoped through `StorageKeys.wholeFileContentObjectKey`.
- `ManifestUpload.SDK.Tests.fs::SmallSourceFileUsesWholeFileContentWithoutStartingManifestSession`
  proves small ineligible files stay on the whole-file path.
- `ManifestDownload.SDK.Tests.fs::WholeFileContentDownloadUsesCompatibilityFallbackWithoutResolvingBlocks`
  proves whole-file downloads do not resolve ContentBlock download URIs.
- `NormalFileMaterialization.Server.Tests.fs::WholeFileContentMaterializesRepositoryObjectIncludingEmptyFile`
  proves whole-file downloads still materialize repository objects.
- `SaveBoundary.Actor.Tests.fs::SaveExpiryPlanningKeepsWholeFileCleanupAsNoOp` keeps whole-file
  cleanup separate from manifest CAS expiry planning.

### Negative Drift And Old-Path Evidence

Status: no blocking stale behavior found in static audit.

- Current `cas/content-blocks` matches are negative assertions or audit text, not current storage
  object-key construction.
- Current OpenAPI examples use `cas/content/<aa>/<bb>/<cc>/<dd>/<full-hash>` for final CAS object
  keys.
- No production migration, old-path reader, import, or grandfathering work is accepted for this
  slice.

### CI And Bot Freshness

Status: orchestrator-owned after this child PR opens.

- This worker can prove local branch freshness, static evidence, generated freshness checks, docs
  lint, and whitespace checks for the #434 commit.
- The orchestrator must open the #434 pull request to `epic/424-storagepool-cas`, wait for CI and
  Codex Code Review Bot on the latest #434 head, and record the result on the pull request.
- After #434 merges, the orchestrator must open the final CAS integration pull request from
  `epic/424-storagepool-cas` to `epic/343-blake3-sha256-version-hashes`, wait for CI and Codex Code
  Review Bot on that latest head, and only then update PR #396 handoff evidence.

### Cleanup And Handoff

Status: ready for orchestrator handoff after #434 validation and review.

- #424 checklist should check #434 only after the #434 PR is merged into
  `epic/424-storagepool-cas`.
- #434 should be closed manually after its epic-branch PR lands, because epic-branch pull requests
  do not auto-close issues on non-default branches.
- The final CAS integration PR should use non-closing `Part of #343` or equivalent wording and
  should not auto-merge PR #396.
- PR #396 body must be updated after CAS lands on #343 with the final CAS integration PR, generated
  freshness, CI, latest Codex Code Review Bot state, and residual-risk evidence.
- Child branch and worktree cleanup remains orchestrator-owned.

## Explicit Waivers

- Runtime code changes are waived for this slice unless validation finds a blocking inconsistency.
  This document is the tracked final proof artifact.
- `pwsh ./scripts/validate.ps1 -Full` is waived for this child PR because it changes only authored
  audit documentation. The final CAS integration PR and PR #396 should carry the broader CI and
  bot evidence after the branch merges forward.
- Production migration, import, grandfathering, compatibility readers, and old-path preservation are
  not applicable. Grace has no production users or production data.
- OpenAPI Generator matrix Markdown under `sdk/generated/matrix/openapi-generator/**` remains
  generated proof output and is not linted as authored Markdown by this slice.
- Managed identity is the cross-account SAS path for pre-production handoff; additional production
  deployment hardening is outside #434 unless the maintainer opens a follow-up.

## Residual Risks

- PR #396 is not ready for maintainer merge until the final CAS integration pull request lands on
  #343 and PR #396 receives fresh CI plus Codex Code Review Bot evidence on its new head.
- The final CAS integration pull request may still find merge or generated freshness drift when
  `epic/424-storagepool-cas` is compared against the then-current #343 branch.
- OpenAPI Generator raw clients remain accepted as proof artifacts behind facades, not stable SDK
  compatibility surfaces.
