# Contributor-Owned Accounting Design

Issue #652 records the accounting decision needed before GV-14 implements contributor-owned billing transfer. This note
captures the current repository content accounting model, the ownership boundary cases, and the implementation concept
that GV-14 should use instead of discovering those semantics during review.

## Current Accounting Model

Grace currently has two cooperating manifest-backed accounting actors:

- `RepositoryContentCounter` is keyed by `(RepositoryId, StoragePoolId, ManifestAddress)`. It counts repository-local
  active references to one manifest-backed file object. Its zero crossings emit repository contribution intents:
  `0 -> 1` emits `IncrementManifestReferenceCount`, and `1 -> 0` emits `DecrementManifestReferenceCount`. Intermediate
  `N -> N+1` and `N -> N-1` transitions stay local to the repository counter and do not fan out to block ranges.
- `ManifestContributionWorkflow` is keyed by the same repository, storage-pool, and manifest address target. It owns
  durable fan-out progress across `ContentBlockRange` entries and emits range active-count deltas only when a range
  succeeds. It is workflow state for retention and recovery, not an owner-specific usage ledger.

The currently counted object level is the manifest-backed file object, identified by `(StoragePoolId, ManifestAddress)`.
Range-level counts exist for physical retention and compaction safety. They do not decide which contributor or repository
owns active usage. `FileVersion`, `DirectoryVersion`, content hashes, and whole-file fallback object keys are not the
right place to attach contributor billing ownership for this slice:

- `FileVersion` and `DirectoryVersion` are immutable content/version records and must not be rewritten to transfer
  ownership.
- `ContentBlockRange` is too physical and may be shared by multiple manifests, so it cannot distinguish intended owner
  scope for a file-level acceptance transfer.
- Whole-file non-manifest content has no matching repository content counter or manifest contribution workflow today.
  GV-14 should limit transfer to manifest-backed content unless a separate issue adds equivalent whole-file active usage
  accounting.

## Decision

The existing actors cannot express owner-specific active counts. They intentionally collapse repeated repository-local
manifest references into one repository contribution and have no `ResourceOwnership`, `CreatorUserId`, or billing-owner
dimension in their command, event, DTO, or primary-key contracts.

GV-14 should add a named ownership ledger concept rather than extending immutable content objects or overloading range
retention state. The recommended implementation concept is `ContentOwnershipLedger`:

- Key the ledger by `(StoragePoolId, ManifestAddress)` so all owner changes for one manifest-backed file serialize
  through one durable stream.
- Store owner-scope entries for active usage, where the owner scope is either repository-owned usage for a repository or
  contributor-owned usage for a creator user in that repository.
- Record accepted transfer operation identities and reject reuse of the same identity with a different payload.
- Treat reapplying the same accepted transfer identity and payload as an idempotent replay.
- Keep repository content counters and manifest contribution workflows responsible for repository reachability and range
  retention. The ledger should consume their stable manifest target and accepted promotion transfer evidence; it should
  not replace them.

The stable transfer operation identity should be derived from the accepted promotion transfer, not from a transient
request retry. It must include the accepted promotion set and the manifest-backed content target, for example:

```text
promotion-transfer:{PromotionSetId:N}:{StoragePoolId}:{ManifestAddress}
```

If GV-14 needs to transfer multiple independently accepted steps for the same promotion set and manifest, include the
step id or terminal reference id in the identity. The same identity must always resolve to the same source owner, target
owner, repository, storage pool, manifest address, and accepted promotion evidence.

## Boundary Classification

| Case | Required accounting result |
| ---- | -------------------------- |
| Abandoned or rejected private branch | No accepted transfer exists, so no repository-owned usage is created. Contributor-owned active usage remains only while the private branch/reference is active. Deletion or expiry decrements the contributor-owned active entry. |
| Same content across contributors | The content identity is the same `(StoragePoolId, ManifestAddress)`, but owner scopes stay separate. Each contributor scope can have one active entry for its own private reachability; repository-owned usage is counted once when accepted into the repository scope. |
| Accepted twice | Reusing the same transfer identity is an idempotent no-op. A distinct accepted transfer for content that is already repository-owned must not double-charge either the contributor or the repository; it should either no-op against the current owner state or record an audit-only replay result. |
| Deletion or retention | Deleting a branch/reference changes active owner usage for the current owner scope only. Physical retention still depends on repository counters, manifest workflows, pending fan-out, and range metadata. Bytes must not be reclaimed while any owner-scope active entry or pending contribution workflow still exists. |
| Reapplying the same transfer identity | Identical identity and payload returns the original decision without a second owner delta. Same identity with a different owner, repository, storage pool, manifest, or accepted-promotion payload is rejected. |

## Contract Propagation For GV-14

GV-14 should update these surfaces if it implements the ledger:

- `Grace.Types`: ledger owner-scope, command, event, DTO, decision, and operation-id contracts.
- `Grace.Actors`: `ContentOwnershipLedgerActor` plus transfer wiring from the accepted promotion apply path.
- `Grace.Server.Unit.Tests`: pure idempotency, duplicate, current-owner, deletion, and replay proof.
- Server integration tests if the accepted promotion transfer crosses hosted HTTP, storage, or background workflow
  boundaries.
- Documentation that describes contributor-owned active usage and deferred billing user docs for GV-15.

The change does not require OpenAPI or CLI surface updates unless GV-14 exposes the ledger directly. Internal transfer
from accepted promotion application can stay server/actor-only.

## Proof Anchors

The executable proof in `ContentOwnershipAccounting.Proof.Tests.fs` locks down the current assumptions:

- repository counters have no owner dimension and collapse same-repository manifest references into one repository
  contribution;
- the same manifest in different repositories has distinct repository counter keys;
- manifest workflows use stable operation identities for idempotent replay and reject mismatched payload reuse;
- range active-count workflow state is retention progress, not owner-specific usage.

These tests are intentionally proof tests for GV-13. They do not implement billing transfer.
