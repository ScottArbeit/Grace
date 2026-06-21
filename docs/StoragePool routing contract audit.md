# StoragePool Routing Contract Audit

Issue #444 closes the #426 mini-epic contract audit for StoragePool-aware CAS routing. Runtime child issues #437
through #443 are merged into `epic/426-storagepool-routing`; this audit records the public contract and generated
artifact evidence that must hold before the #426 integration branch can target `epic/424-storagepool-cas`.

## Branch Flow

This issue branch was created from current `origin/epic/426-storagepool-routing`, and its pull request must target
`epic/426-storagepool-routing`, not `main`, not `epic/424-storagepool-cas`, and not
`epic/343-blake3-sha256-version-hashes`. The final #426 integration PR will target `epic/424-storagepool-cas` after
this child lands.

## Contract Inventory

| Surface | Evidence | Audit result |
| ------- | -------- | ------------ |
| Runtime contracts | `src/Grace.Types/UploadSession.Types.fs`, `src/Grace.Types/ContentBlockMetadata.Types.fs` | `UploadSessionDto`, `FileManifest`, `ContentBlockMetadata`, `ClaimedReuseRange`, and `ContentBlockStoragePlacement` carry server-owned StoragePool and placement evidence. |
| Server routes | `src/Grace.Server/Storage.Server.fs` | Upload, confirm, finalize, cleanup, and download flows use repository authorization plus server-selected StoragePool route evidence. |
| OpenAPI source | `src/OpenAPI/Storage.Components.OpenAPI.yaml`, `src/OpenAPI/Storage.Paths.OpenAPI.yaml` | StoragePool, session, manifest, placement, and targeted download fields are represented in source components and paths. |
| Bundled OpenAPI | `src/OpenAPI/Grace.OpenAPI.yaml`, `src/OpenAPI/Grace.OpenAPI.3.1.2.yaml` | Regenerated from source by `pwsh ./src/OpenAPI/generate-openapi-projections.ps1`; proof manifest records freshness. |
| Generated SDK matrix | `sdk/generated/matrix/openapi-generator/{typescript-fetch,python,rust}` | Generated storage clients expose `StoragePoolId`, `UploadSessionId`, `AuthorizedScope`, `Manifest`, and `StoragePlacement`; shared `FilePath` remains in bundled OpenAPI outside the #426 storage route models. |
| F# SDK helpers | `src/Grace.SDK/Storage.SDK.fs` | SDK helpers call server routes for session-scoped upload/download and parse server-provided shard evidence before host-shape inference. |
| CLI fixtures | `src/Grace.CLI.Tests` | No public CLI command lets a caller choose physical StoragePool placement; existing CLI storage behavior remains SDK-mediated. |
| Narrative docs | `CONTEXT.md`, `README.md`, `docs/adr/0001-content-addressed-storage-contentblocks.md` | Docs distinguish repository-scoped whole-file content from StoragePool-wide manifest-backed CAS dedupe. |

## Cross-Surface Invariants

- `StoragePoolId` is server placement evidence. Public parameters may carry recorded evidence for an existing session or
  manifest, but they do not grant caller authority to choose physical placement.
- Shard account, container, and object key evidence are selected by the server route and recorded as
  `ContentBlockStoragePlacement`. SDK parsing prefers the server-provided `graceStorageAccount` URI fragment before
  Azurite or host-shape inference, which protects custom, CNAME, and private-link endpoint forms.
- ContentBlock object keys use the current CAS fanout shape:
  `cas/content/<aa>/<bb>/<cc>/<dd>/<content-block-address>`. The OpenAPI examples use this shape and do not describe
  `cas/content-blocks` as current storage behavior.
- Manifest ownership is scoped by repository authorization plus `FileManifest.StoragePoolId`. Finalize and annotation
  materialization read metadata by `(StoragePoolId, ManifestAddress, ContentBlockAddress)` instead of using the current
  repository route as a shortcut.
- Upload lifecycle evidence is session-owned. `UploadSessionId` and `AuthorizedScope` are recorded when the session
  starts and must match on later upload, discovery, claim, confirm, finalize, cleanup, and replay paths.
- Download authorization is targeted to recorded manifest evidence. `getContentBlockDownloadUri` accepts
  `AuthorizedScope`, `StoragePoolId`, `ContentBlockAddress`, and `ManifestAddress`; it does not repost a full manifest
  per block and does not expose global dedupe state as an authorization oracle.
- Equivalent ContentBlock encodings are validated by decoded content and address checks before durable acceptance; the
  contract does not claim whole-file content is globally deduped.

## PR 446 Trap Audit

| Trap | Evidence | Result |
| ---- | -------- | ------ |
| Custom or CNAME blob endpoints | `Storage.SDK.fs` parses `graceStorageAccount` URI fragments before configured endpoint fallback. | Covered; account identity does not depend only on host shape. |
| Path-style Azurite parsing | `Storage.SDK.fs` keeps path-style parsing for localhost/IP hosts and only uses the first path segment as account when fragment evidence is absent or matching. | Covered. |
| Private-link endpoint suffixes | Server SAS issuance uses selected shard placement, and SDK account evidence comes from the fragment instead of rewriting suffixes from a guessed host. | Covered by route and SDK contract; no public suffix rewriting surface changed in #444. |
| Staging namespace and cleanup | `Storage.Server.fs` uses repository and upload-session scoped staging placement and cleanup paths. | Covered by #437-#443 runtime tests; no new runtime behavior in #444. |
| Final CAS reuse and range safety | Finalization loads authoritative metadata for claimed ranges and validates StoragePool, address, metadata version, range, placement, and payload bytes. | Covered by server unit tests for finalize hydration and replay safety. |
| Confirm rollback and non-deletion safety | Confirm/finalize cleanup retains final shared CAS when deletion could remove content referenced by another session or finalized manifest. | Covered by #446/#426 runtime fixes; #444 records the contract without adding broad behavior. |
| Targeted download authorization | Download URI parameters use manifest identity and targeted scope instead of reposting full manifest payloads per block. | Covered by OpenAPI and F# SDK route shape. |

## Generated Freshness Plan

- Run `pwsh ./src/OpenAPI/generate-openapi-projections.ps1` after OpenAPI source changes.
- Run `pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending` and name existing pending gates.
- Run `pwsh ./sdk/scripts/invoke-generator-matrix.ps1` if OpenAPI projection changes affect generated SDK artifacts.
- Run MarkdownLint for this document with the repo MD013 120-character convention.
- Run `git diff --check`.
- Use `pwsh ./scripts/validate.ps1 -Fast` as the final build/test gate because #444 changes contract/docs/proof
  artifacts only. `-Full` is waived unless a later fix exercises storage-emulator runtime behavior.

## Explicit Waivers

- Production migration and old-path compatibility are not applicable. Grace has no production users or production data,
  and this audit records the clean-forward contract.
- Caller-selected physical placement is not applicable. Public APIs may echo or require recorded StoragePool evidence,
  but the server owns route selection.
- New runtime behavior is waived for this slice unless validation finds a blocking inconsistency. The runtime child
  issues #437 through #443 own the behavioral tests and fixes.
- New CLI commands or fixtures are waived. The audited storage flow remains SDK-mediated, and there is no CLI public
  command that accepts a physical `StoragePoolId` placement choice.
- Full storage-emulator validation is waived for this docs/OpenAPI contract slice unless a runtime fix is added.
