# Libraries design

Status: Design accepted by Scott on 2026-09-06, with zero-byte local file exclusion accepted on 2026-09-07. Issue #1042 is complete: [PR #1053](https://github.com/ScottArbeit/Grace/pull/1053), reviewed at `c58dfbe0ab5cf27bd4d351f7af8355628d3eb1f3`, merged as `63b07c60de9e1fb4429f7855882febe0ed39f0a5`. Issue #1039 implements the Windows client against that merged contract.

Quality: Product V1. This document is the maintained Library design for [Epic #1037](https://github.com/ScottArbeit/Grace/issues/1037). It replaces the implementation instructions in revision 0.32 and the old Issue #1042 repair scope. The supplied revision 0.32 files remain historical source material, unchanged in Downloads. The HTML review remains the detailed assessment of the older code.

## Outcome and delivery

An authorized participant edits ordinary nonempty files in a repository-owned Library. Grace transfers accepted bytes and changes between participating working copies without creating Saves, References, DirectoryVersions or WorkItem Attachments. Local zero-byte files are excluded before pending input or upload preparation; they remain present and protected from incoming overwrite or deletion. Previously captured nonempty input remains durable after a later zero-length save.

One configured repository-relative root is one Library. A repository owns a sorted catalog of at most 128 non-overlapping roots. Adding a root requires an empty outgoing version-control namespace; removing it requires an empty Library namespace. A catalog change changes path ownership, never imports or moves files.

The current user-visible slice is two authorized Windows 11 working copies, one initially empty Library, create in A, edit in B, then restart both without another logical change or filesystem rewrite. Scott authorized dependent Issue #1039 work before the server merge. That client work now continues toward a separate mainline PR; its own review and merge approval remain required.

Later epic work covers participation/offline behavior, broader namespace competition and ownership transitions, and Linux/macOS conformance. Public history browsing, restore, diff, search, AI assistance, Cache, placeholders and on-demand hydration remain deferred. No new WDU caller, database, general locking service, migration layer or alternative platform state machine is included.

## Accepted choices

All replacement choices in the report are accepted, including compact item/slot history, immutable-version signed reads, notification progress and the three-table client. They are subject to the listed experiments, not another approval of the same choices.

- One `RepositoryLibraryActor` orders Library changes for each repository.
- Every Library metadata write and clear goes through the registered Orleans provider from that actor.
- Cosmos SQL performs aggregate and cross-record reads when Cosmos is configured. No actor directory substitutes for queries.
- Stable item identity and parent/name relationships survive renames and moves.
- Content revision records edit order; deterministic content identity permits equal bytes to share storage.
- Every baseline build may pause Library changes until its complete manifest is published.
- Keep accepted change records and referenced historical bytes permanently. History indexes can be rebuilt.
- Keep one send-progress cursor and at most one failed envelope. Notifications remain advisory.
- Local persistence uses repository state, materialized items and durable operations in the existing SQLite database.
- Keep established upload, content-counter and manifest-workflow owners. No per-record Library actors remain.
- Use the [type plan](design/Libraries.Type-Plan.md) to apply the 173 reviewed dispositions. The counts are an inventory, not a quota.

## Evidence and implementation status

The following table records the historical inputs to the accepted redesign on 2026-09-06. The current server delivery is PR #1053, identified above; the older PRs remain preserved source material.

| Evidence | Revision and treatment |
| --- | --- |
| GitHub main | `975ec65b188702d5cb7de3b2220e589d4957b565`, checked 2026-09-06; Issue #1038 is already merged. |
| Server candidate | PR #1043 at `6399d4db015085a1185eb7e367a8d24ece6ddbaa`; preserve as a source of selected changes, replace before merge. |
| Client candidate | PR #1044 at `2d7ca7abfabe1a84f057c8d3a533c87bb0b5d42e`; preserve tested behavior, change contracts and local records before resuming delivery. |
| Orleans package | `Microsoft.Orleans.Persistence.Cosmos 10.2.2-hpk.1`, approved source `12c6f7ea413eede43c1f1a589b51430fcf9ae7ed`; preserve exact package/source and CI restore access. |
| Existing CI and tests | Apply only to the revisions above. They do not certify this replacement. |
| New experiments | [Storage experiment results](design/Libraries.Storage-Experiment.md); distinguish emulator, model and unexecuted integration cases. |

There is no production Library data to migrate. Development fixtures may be regenerated deliberately. Other local state, branches and worktrees remain outside this redesign.

## Authorization, public behavior and bounds

Repository administrators manage the catalog. Library readers may read permitted catalog, items, slots, pages, receipts, bytes and wake hints; writers may also prepare bytes and submit changes. Recheck repository identity and permissions at disclosure, acceptance and content transfer. Preserve missing/cross-repository no-oracle responses and the existing content integrity checks.

Keep the fifteen existing `/libraries` routes and their SDK operations. Change their Library DTOs together as described below, including typed upload preparation, immutable content reads and revision preconditions. Update HTTP validation, OpenAPI, generated TypeScript/Python/Rust clients and examples in the same remote delivery. Public cursors and page/read tokens stay opaque, repository-bound, purpose-bound and signed; page/read tokens expire. Token keys remain deployment configuration, stable across server instances.

Keep stable operation IDs. Normalize and hash each complete request once. Identical retries return the same recorded result; different input under the same operation ID is rejected. Receipt recovery precedes upload-expiry checks.

Library roots use relative path segments, reject absolute/traversal/reserved metadata paths, and compare normalized names using the accepted NFC and ordinal-ignore-case rules. Root matching respects segment boundaries: `shared` owns `shared/file.txt`, not `shared-old`. Full item paths are derived from parent links. The path limit is 1,024 UTF-8 bytes. The repository limit is 100,000 retained item heads and 100,000 retained slots, counting tombstones and vacancies.

Keep content-free status and `LibraryContentAvailable.v1` wake behavior. A wake prompts a pull; only accepted changes determine order. There is no promise of immediate notification after an idle silo restart without a request or existing activation trigger.

Keep `grace library` catalog commands and add `grace library sync enable|run|status` in Issue #1039. Preserve the normal human and `cli-json-v1` output conventions. Disable/offline/re-enable behavior belongs to a later selected epic slice.

## Identify each item by its parent and name

`LibraryNamespaceDto = { Parent; Name; NamespaceVersion }`. A root parent names a configured Library path; an item parent names a stable directory item. A slot is keyed by `(repository, parent identity, normalized sibling name)`. Full paths are derived views. Remove `NormalizedPath` from the shared namespace/slot contracts and update the client to resolve the parent graph; do not store a path that silently becomes stale after an ancestor moves.

Example: directory D is `assets/design`; file F has parent D and name `logo.svg`. Moving D to `shared/design` changes D's parent edge and its two affected slots. F still has parent D. Its path becomes `shared/design/logo.svg` without rewriting F or manufacturing a content revision. A client with the current item graph derives the same result. Historical paths use the graph at the historical cursor.

Before moving a directory, walk the destination’s parent chain and reject a move into the directory itself or one of its descendants. Check that the current catalog allows both locations. To enforce the 1,024-byte path limit, read the bounded current namespace while the actor holds its turn and calculate the new paths of affected descendants. This requires a query and an in-memory traversal, but no writes to descendant records. Use SQL to check for live children before deleting a directory. Check that a Library is empty inside the actor before removing its root; an earlier HTTP query can be stale.

For a stale-content conflict, compute a deterministic candidate sibling name, read that exact slot, and try deterministic numbered alternatives while occupied. Select and persist the winning name and new item ID in Pending before any projection writes. A user-created filename may collide with the first candidate. Retries reuse the selected result; they never reallocate a different sibling after acceptance.

Historical review evidence: [conflict allocation](https://github.com/ScottArbeit/Grace/blob/6399d4db015085a1185eb7e367a8d24ece6ddbaa/src/Grace.Server/Library.Coordinator.Server.fs#L791) had no occupancy read for its generated name; [rename/move](https://github.com/ScottArbeit/Grace/blob/6399d4db015085a1185eb7e367a8d24ece6ddbaa/src/Grace.Server/Library.Coordinator.Server.fs#L830) changed only the selected item's stored path; [slot projection](https://github.com/ScottArbeit/Grace/blob/6399d4db015085a1185eb7e367a8d24ece6ddbaa/src/Grace.Server/Library.Coordinator.Server.fs#L284) hashed that path. The accepted parent/name model replaces these inherited behaviors.

## Keep useful public distinctions; remove nested copies

| Contract | Recommended shape | Removed duplication |
| --- | --- | --- |
| Item | `{ ItemId; ItemKind; LastChangeCursor; Namespace?; Content?; Tombstone? }`, plus the accepted separate ContentRevision. | Derive live/tombstoned state. Catalog version belongs to the catalog/change, not every current item. Validate live file, live directory and tombstone combinations centrally. |
| Namespace slot | `{ Parent; Name; SlotVersion; OccupantItemId? }`. | Derive occupied/vacant. Slot version stays here instead of being copied into the item's namespace. |
| Tombstone | `{ DeletedAt; DeletedBy; DeleteCursor; LastNamespace; LastContentVersionId? }`. | Item ID/kind already belong to the containing item. Preserve the last parent edge, not only its version. |
| Accepted change | `{ OperationId; ChangeKind; AcceptedAt; AcceptedBy; LibraryCatalogVersion; Item; Conflict? }`. | One resulting item replaces parallel item ID/kind/namespace/content/tombstone fields. Its LastChangeCursor is the public change cursor. |
| Conflict provenance | `{ OriginalItemId; BaseContentVersionId?; BaseContentRevision? }`. | Source operation, resulting conflict item/path/content and acceptance instant are already in the change. Keep the causal relationship, not another snapshot of the result. |
| Operation receipt | Operation/hash/outcome plus one accepted Change or a rejection's reason/catalog/rebaseline evidence. | Remove parallel Item, Cursor, Conflict and accepted metadata copies. The accepted storage record references its accepted change. |
| Prepare content | `{ UploadSessionId; Blake3Hash; Sha256Hash; Size; AuthorizedScope; StoragePoolId; ExpiresAt }`. | No second prepared-content identity, always-true UploadRequired or JSON string containing another anonymous request contract. SDK methods already name the upload routes. |
| Read content | `{ DownloadPath; Content; ExpiresAt }`, named `LibraryContentReadDto`. | No duplicated GrantId token or consumed-grant fiction. The token binds a retained immutable version. |

**Check that the requested bytes belong to an accepted revision.** Add `ContentRevision` to the read request. Decode its repository-bound cursor, read the accepted-change record, and check the item and content IDs. This still works after a later edit changes the item. A content-location record left by an unaccepted upload is not enough to issue a read URL. Once issued, the URL names that accepted content until expiry.

The small parent, content, tombstone, precondition and result DTOs remain part of the design. Keep public parameter classes and SDK/CLI command adapters where Grace uses them consistently. Do not turn them into a single optional-field request or add a hand-written enum beside every OpenAPI enum. Since Library is unused, change the Library contract coherently instead of adding compatibility aliases or version-translation layers.

## Persistence model

Keep six storage purposes and the accepted full partition keys. The registered Orleans provider owns outer document ID, grain type and partition fields. Each inner state keeps its schema version and semantic fields; do not repeat outer routing merely because the old direct Cosmos document did. The table below is the complete accepted Library persistence roster, including embedded and transient coordination. Every metadata write and clear is called from RepositoryLibraryActor through the registered provider.

| Record / shape | Fields and key | Owner, readers and lifetime |
| --- | --- | --- |
| LibraryControlDocument | `{ SchemaVersion; Catalog; Epoch; CommittedCursor; ReplayFloor; Pending?; ItemRecordCount; SlotRecordCount; HistoryThrough; NotifyThrough }`. One control document; PK `[repo]`. Derive next cursor as CommittedCursor + 1 once pending repair completes. | The repository actor creates, reads and conditionally updates the catalog, change order and pending work. Keep the record for the repository’s lifetime. Update the item and slot counts by the stored pending deltas when the change completes; counts include tombstones and vacant slots. |
| LibraryPendingDecision; rename existing Pending type | `ItemChange of LibraryAcceptedChangeRecord \| CatalogChange of operation/hash, expected and chosen catalog version, add/remove path, accepted time/principal`. Embedded in control; at most one. The catalog case stores a deterministic delta against the retained catalog, avoiding two complete 128-root arrays in control. | The actor saves the complete decision before performing its writes, reads it during recovery and clears it when complete. It has no separate key, partition or actor. Temporarily keeping the chosen result here allows recovery if the accepted-change or result record has not yet been written. |
| LibraryAcceptedChangeRecord | `{ SchemaVersion; Cursor; RequestHash; CorrelationId; Change; PriorNamespace?; PriorContentVersionId?; ConsumedNamespaceVersion?; ConsumedContentVersionId?; ConsumedContentRevision?; ConsumedSlotVersion?; AddedItemRecord; AddedSlotRecord }`. PK `[repo, D20((cursor-1)/200)]`, ID by cursor. | The actor writes one immutable record per accepted item change. Recovery, change retrieval, receipts, history and notifications read it. Keep it permanently, even after the replay floor advances. The added-record flags preserve the original capacity decision if only some writes complete. |
| LibraryCurrentItemDocument | `{ SchemaVersion; Item; LastCursor; HistoryTailSegment? }`. PK `[repo,item]`; ID by item ID. Maximum 100,000 remembered items. | The actor writes current items; point reads and SQL queries read them. LastCursor prevents an older change from replacing a newer one and lets readers check which write they can see. Deletion writes a tombstone. Keep the history-tail pointer when updating an item, and keep the latest item and cursor when updating its history pointer. |
| LibraryCurrentSlotDocument | `{ SchemaVersion; Slot; LastCursor; HistoryTailSegment? }`. PK `[repo,slot]`; ID by hash of a one specified length-delimited encoding of parent and name. Maximum 100,000 remembered slots. | Actor creates/updates; admission reads exact occupancy/vacancy. Derived projection with a retained generation that protects stale creates. Store/check the full parent/name to detect a key collision; never equate hash equality with identity. No routine deletion. |
| LibraryReceiptDocument + new LibraryOperationOutcome | `{ SchemaVersion; OperationId; RequestHash; Outcome }`, where Outcome is `AcceptedChange of int64 \| RejectedChange of LibraryOperationReceiptDto \| CatalogResult of LibraryCatalogChangeResultDto`. PK `[repo,operation,operationId]`; one fixed document. | Actor writes once or verifies exact retry. Operation callers read it; accepted responses read one journal record. Permanent retry/audit lookup. Catalog results retain catalog provenance. Pending guards terminal visibility; no provisional-result upgrades. |
| LibraryContentLocationDocument | `{ SchemaVersion; Content; AuthorizedScope; Manifest }`. PK `[repo,content,contentVersionId]`. | The actor reads a completed upload session and creates or reuses the stored content descriptor and manifest. Reference accounting and downloads read this mapping. Keep one per repository content identity for accepted historical use. The full manifest remains available after temporary UploadSession state is deleted. |
| LibraryHistorySegmentDocument | `{ SchemaVersion; PreviousSegment?; Cursors:int64[] }`. PK `[repo, item:id or slot:key, D20((cursor-1)/512)]`. At most 512 entries and 900 KiB of real serialized state/envelope. | Actor appends idempotently from journal; exact-key history readers traverse nonempty segments. Rebuildable permanent index. Update the item's/slot's tail pointer after its segment is durable, and advance HistoryThrough after all pointers. No copied LibraryHistoryEntry payload. |
| LibraryBaselineShardDocument | `{ SchemaVersion; Items }`; boundary identity in deterministic record key. PK `[repo,baselineId,shardOrdinal]`. At most 1,000,000 bytes under the chosen exact serialization. | Actor creates immutable sorted pages while holding the baseline write pause. Public baseline readers read only published shards. No record actor and no durable build-state type. Counts/bytes are computed, not copied into shard state. |
| LibraryBaselineManifestDocument + new LibraryBaselineShardReference | `{ SchemaVersion; Epoch; BoundaryCursor; Catalog; CreatedAt; Shards }`, with each shard reference `{ Ordinal; Blake3Hash; ItemCount }`. Manifest PK `[repo,baselineId,manifest]`. | The actor writes this record after all shards. Readers treat its existence as a published baseline and verify each named shard. Derive the baseline ID from repository, epoch, cursor and catalog version. Record the build time; if a complete manifest already exists, return it on retry. Keep abandoned and superseded baselines under the current retention policy. |
| FailedGraceEventEnvelope | Exact body, content type, stable MessageId and string application properties. PK `[repo,failed-event,single]`. At most one, for NotifyThrough + 1. | Actor stores only after terminal SDK send failure, retries exact bytes, and clears after successful progress is durable. Coordination, not accepted Library state. Journal backs unsent work; no successful-event outbox record or separate fallback actor. |
| LibraryUploadPreparation; embedded in existing UploadSession | `{ OperationId; PrincipalId; ExpectedSha256; ExpiresAt }`. Reuse session ID, expected BLAKE3, size, scope and pool. | UploadSession handles preparation, retries, completion and cleanup. The Library actor checks the session before accepting a new change. No additional Library container record is needed. Keep the fixed expiry and explicit serializer field IDs. |

**Why keep both journal and current records?** The journal answers what was accepted and supports exact retry/history. Current records answer point/aggregate queries without replaying a repository's lifetime. Their duplication buys a necessary access path. Pending buys crash recovery; receipts buy operation-ID lookup; history cursor lists buy targeted access; immutable baselines buy stable paged transfer. The removed indexes and repeated payloads provide weaker benefits at substantially more cost.

**History policy status:** the accepted Product V1 design keeps compact indexes, both tail fields and `HistoryThrough`. The permanent journal and historical content remain independently durable.

## New declarations

| Added type | Creation, use and lifetime | Why add it |
| --- | --- | --- |
| LibraryOperationOutcome | Actor constructs it; operation lookup and repair match its three cases. Embedded once in the operation record at its key/partition, retained for repository lifetime. No separate identity or owner. | Represents three mutually exclusive durable outcomes. It replaces optional/cross-record catalog bridging. Neither old issue explicitly names it; this is a deliberate redesign choice. |
| LibraryBaselineShardReference | Builder creates; manifest reader consumes. Embedded in one baseline manifest for that baseline's retained lifetime; no independent CRUD/key/partition. | Keep ordinal, hash and item count together. One record replaces three arrays whose corresponding entries must otherwise remain aligned. |
| LibraryStorageFixture (test only) | Test runner creates/destroys it per scenario. Implements existing IGrainStorage with separate durable snapshots and attempt buffers; keys mimic the six provider purposes. No production store or actor. | Replace several per-interface fakes with one realistic persistence boundary. Test actual provider JSON separately; an in-memory fixture alone cannot certify Cosmos consistency. |

LibraryPendingDecision and LibraryContentPreparationDto/LibraryContentReadDto are renames/replacements of existing entries, not additional parallel contracts. ContentRevision uses the existing LibraryCursor type. Do not add an effect union, generic workflow engine, record actor or new grant/preparation document beside these.

## One acceptance protocol, with explicit crash behavior

1. **Enter the serialized repository turn and repair.** Revalidate the repository identity and authorization. Finish any earlier Pending. Look up this operation ID and compare its normalized request hash before resolving temporary upload state. An exact retry returns the recorded result even after preparation expiry; different input under the same ID fails.
1. **Check the current state before choosing a result.** For a new command, check the catalog, parent chain, namespace and content versions, destination vacancy and capacity. File commands also check the completed UploadSession’s operation, principal, hashes, size and expiry, then create or reuse the stored content mapping. An upload does not itself change the namespace. If the mapping is written but reservation fails, it remains unused under the current no-cleanup policy.
1. **Reserve one complete decision.** CAS control with Pending, including the chosen item ID, result, slot generations, cursor, timestamps, correlation and admission deltas. Before that write, a failure accepts nothing. After it, recovery completes that decision rather than reconsidering its outcome.
1. **Write the accepted-change record.** Create it at the reserved key. If the write’s result is uncertain, reread and compare it; stop if the stored record differs. A changed in-memory object does not establish that the storage write succeeded.
1. **Retain accepted content before publication.** Use the existing counter's tracked add and manifest workflow, with stable operation identity. The original zero-transition revision remains recoverable across later ordinary references and absent Redis. Wait for the existing workflow's completion. Each accepted content-bearing operation contributes one permanent historical reference; do not optimize this to one per byte identity in this redesign.
1. **Write deterministic synchronous projections and the operation result.** Upsert the resulting item and at most the old/new slot by cursor; write the accepted receipt reference. Same cursor requires the same semantic value, later cursor never regresses. Replaying a finished receipt must skip re-adding the contribution. Acknowledge the tracked add only after workflow and receipt durability.
1. **Complete control.** Advance CommittedCursor and admission counts by the stored deltas and clear Pending in one CAS. Only then may an accepted result be returned as terminal or exposed by committed delta/current reads. A crash before completion replays the same decision; receipt presence alone is insufficient.
1. **Update history and send notifications in bounded batches.** Read accepted changes to fill history indexes and construct notification messages. Send each message first; save its envelope only after the SDK reports a terminal failure. Acceptance can continue while history is behind or Service Bus is unavailable. Activation and the existing repair, status and operation paths resume this work. An Orleans timer may continue a drain while the actor is active; no new global scheduler is added.

**Catalog changes use the same Pending slot.** The actor checks the current expected catalog version and Library emptiness itself. For an accepted change, record the chosen catalog delta in Pending, derive and persist the exact unified operation result, then CAS the new catalog and clear Pending. Rejection is an immutable operation result with no catalog mutation. Request replay repairs before reporting success. Move catalog decision-making out of [the HTTP handler](https://github.com/ScottArbeit/Grace/blob/6399d4db015085a1185eb7e367a8d24ece6ddbaa/src/Grace.Server/Library.Server.fs#L390); remove the catalog-specific provisional/committed bridge. For version-control emptiness, supply immutable Reference/DirectoryVersion evidence through a narrow function and respect the already accepted IsInLibrary race; do not create a cross-domain reservation or call RepositoryActor back from its creation workflow.

| Failure point | Durable evidence | Required recovery |
| --- | --- | --- |
| Reservation response lost | Control either contains the complete Pending or does not. | Reload control before choosing again. Same operation cannot acquire a different ID/cursor/result. |
| Accepted-change or projection write throws | Fresh provider state + ETag, not a shared mutable buffer. | Discard the candidate buffer, reread and compare. Continue only from confirmed durable state; deactivate on unexplained conflict. |
| Counter committed, workflow not started | Existing PendingTrackedAdd retains operation and zero-transition revision. | Resume that exact workflow, without an extra reference increment. |
| Receipt exists, control still Pending | Saved decision and receipt reference agree; tracked completion may already have occurred. | Skip re-add, finish acknowledgement idempotently, complete control, then return success. |
| Commit done, first notification never sent | CommittedCursor > NotifyThrough; accepted-change metadata retained. | Next activation/recovery resumes from NotifyThrough + 1. No scan over every possible historical fallback actor. |
| Send succeeds, progress write fails | NotifyThrough has not advanced; failed envelope may or may not exist. | Resend with stable MessageId. Duplicate transport delivery is allowed. Consumers use accepted cursor/order. |
| Fallback clear fails after progress advances | NotifyThrough records that the send succeeded; the saved failed envelope belongs to an earlier cursor. | Clear the obsolete fallback before attempting the next one; never mistake it for the current unsent change. |

**NotifyThrough is an intentional addition.** It replaces unbounded per-cursor fallback probing with one recoverable position and one failure slot. It adds a small successful-send progress write to control, but no successful-send envelope/outbox record. It is not interchangeable with HistoryThrough: a Service Bus outage must not block history indexing. This accepted change replaces the old implementation; Orleans does not require it. An idle repository still needs an existing activation/request trigger after a silo restart; this design does not claim guaranteed wall-clock notification delivery without a durable wake mechanism.

## Baselines: use the approved pause to remove the race

1. In one non-reentrant RepositoryLibraryActor call, finish Pending and capture committed boundary B, epoch and catalog. Reuse a complete baseline at the deterministic key if present.
1. Establish current-partition read visibility. The SQL client and providers share a singleton CosmosClient per silo. On a fresh activation, validate the control ETag; for B > 0, point-read the last accepted change's item using that query client until its LastCursor equals B. No later Library write can occur during this turn. Failure to establish visibility aborts the build; it does not publish a partial snapshot.
1. Enumerate every current-item record, including tombstones, with parameterized SQL and continuation paging in full partition `[repo,item]`. Verify the admitted record count and that no item cursor exceeds B. Resolve live parent edges and reject a malformed live graph. Retained tombstones may refer to formerly configured roots; do not require historical edges to belong to the current catalog. Current state size, not lifetime history, bounds this work.
1. Sort deterministically, serialize through the real configured codec, pack byte-bounded shards and persist them through Orleans. Hash the exact defined shard bytes with BLAKE3. Derive shard IDs; a retry must not depend on a failed build's volatile timestamps.
1. Write the complete manifest last. The manifest's existence is the publication point. No control baseline pointer, separate snapshot actor, double collect, concurrent-tail overlay or durable build status is needed.
1. Release the turn. Serve requested pages by manifest shard counts, loading only intersecting shards. Validate token repository/purpose/baseline/offset/expiry. Old published baselines remain valid for their token lifetime; they are immutable even while new changes resume.

Build cancellation before publication leaves only unpublished shards. Exact keys permit a retry at the same boundary; if the repository has advanced, build the new boundary and leave old residue under the accepted retention policy. Measure elapsed pause, peak memory, RU, request timeout/retry behavior and serialized headroom at 100,000 items. The owner approved a pause, not an unmeasured latency promise. No temporary concurrency framework is warranted before those measurements.

## Cosmos SQL reads are part of the design

Keep the full partition key on each query. The approved provider stores the outer fields as `PartitionKey`, `PartitionKey2`, `PartitionKey3` and `State`; check their casing with the configured JSON serializer. Set the complete SDK request partition key as well as the SQL conditions. Index the fields these queries use.

```text
-- Current namespace, with continuation paging; PK [repo, "item"]
SELECT c.State FROM c
WHERE c.PartitionKey = @repo AND c.PartitionKey2 = "item"
ORDER BY c.id

-- Directory emptiness; PK [repo, "item"]
SELECT TOP 1 VALUE c.id FROM c
WHERE c.PartitionKey = @repo AND c.PartitionKey2 = "item"
  AND IS_NULL(c.State.Item.Tombstone)
  AND c.State.Item.Namespace.Parent.Kind = "item"
  AND c.State.Item.Namespace.Parent.ItemId = @directoryId

-- One ordered segment of accepted changes; PK [repo, segment]
SELECT c.State FROM c
WHERE c.PartitionKey = @repo AND c.PartitionKey2 = @segment
  AND c.State.Cursor > @after AND c.State.Cursor <= @committed
ORDER BY c.State.Cursor
```

The storage experiment observed that the configured F# option converter writes `None` as JSON `null`, including under `WhenWritingDefault`. Use `IS_NULL(...)` for the live-item test. The `NOT IS_DEFINED(...)` control query returned zero for these records. Test the exact nested production paths when the replacement shapes are implemented. Read only the change segments needed for a page. Use point reads for operation, content and baseline records, and follow known segment links for history. One fixed failed-message key avoids a cross-partition scan. The two counts in control enforce capacity; count queries can check those values during validation.

Separate Cosmos clients do not automatically see each other’s recent writes. Sharing a client within a silo helps, but a newly started silo still needs the explicit visibility check. The provider exposes its ETag operations in the approved [CosmosGrainStorage source](https://github.com/ScottArbeit/orleans/blob/12c6f7ea413eede43c1f1a589b51430fcf9ae7ed/src/Azure/Orleans.Persistence.Cosmos/CosmosGrainStorage.cs). See [Microsoft's session-token guidance](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency#utilize-session-tokens). Test these reads with independent clients and the actual provider JSON before implementation.

## Code and actor ownership

```text
HTTP / SDK / CLI
  → RepositoryLibraryActor (one per repository)
      → LibraryDecision functions: validate and choose
      → LibraryRecords functions: typed registered Orleans provider operations
      → LibraryQueries functions: explicit Cosmos SQL reads
      → LibraryTransfer functions: existing upload / counter / workflow actors
      → LibraryTokens functions: signed cursor, page and read tokens

Existing shared owners retained:
  RepositoryActor → initializes the Library catalog (no callback cycle)
  UploadSessionActor → temporary transfer and cleanup
  RepositoryContentCounterActor → reference count + tracked handoff
  ManifestContributionWorkflowActor → existing range activation
  ContentBlock/range/storage owners → existing immutable bytes
```

Use `RepositoryLibrary.Actor.fs` for the actor and small actor-facing helpers. Move the decision/record/query implementation out of Server services into F# modules in the Actors project, in explicit compile order before the actor. Server keeps HTTP adaptation and composition. A module file is not another actor. Remove `LibraryRecord.Actor.fs` after its 13 classes are gone; do not hide a generic union/object grain there. The existing one-domain-per-actor-file convention remains clear.

`IRepositoryLibraryActor`, existing upload/counter/workflow grain interfaces, `IGrainStorage` and `IDocumentIdProvider` are framework or distributed boundaries. Keep them. `ILibraryStore`, `ILibraryTransferStore`, `IGraceEventSender` and `ILibraryManifestContributionActivator` are application abstractions introduced by composition/testing choices. Replace them with functions that own a small behavior. Passing a send or authorization function where needed is enough; do not replace four interfaces with a giant “dependencies” record containing all their members.

The existing [IGrainStorage API](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Core/Providers/IGrainStorage.cs) accepts typed state, state name and GrainId. Private functions can address deterministic record keys without activating a grain for each key. Those functions use the registered provider after Orleans initializes it. Create a fresh `GrainState<T>` for each attempt. Check the repository and key, use the last-read ETag, and reread storage after an uncertain write. The [standard state bridge](https://github.com/dotnet/orleans/blob/v10.2.2/src/Orleans.Runtime/Storage/StateStorageBridge.cs) additionally supplies runtime checks, tracing and migration behavior. Direct provider calls need logging and metrics. Keep these records out of cached activation and migration state. The public API supports this approach, but a Grace prototype still needs to exercise writes, failures and restarts.

## Alice editing while Bob's changes arrive

**Server acceptance order decides when an edit enters Library history. SignalR arrival time and the time someone opened an editor do not.** A local edit keeps the base against which its bytes diverged. Receiving a newer server head must not silently rewrite that base.

| Step | Server | Alice's disk and local facts |
| --- | --- | --- |
| Initial | r1 contains X. | Disk X; last materialized base r1/X. |
| Alice saves her edit | Still r1. | Disk Z differs from X. Pending local operation records r1 as its content base and freezes the exact candidate bytes before upload. |
| Bob submits Y, then X | Accepts r2/Y, r3/X. | Notification wakes catch-up. Received changes can be recorded as pending remote work. Preserve Z and r1; do not claim that r2/r3 have been applied to disk. |
| Alice submits Z | Submission is considered after r3. Byte identity is X in both r1 and r3, but their edit history differs. | With revision comparison, her request is stale and Z becomes a deterministic sibling conflict item. With byte-state comparison, Z replaces current X. Revision comparison is the owner-accepted choice. |
| Finish locally | Accepted result is durable and retryable. | Preserve/publish the accepted sibling when needed, install the server’s current version at the original path only when safe, and advance the applied cursor after the relevant filesystem/SQLite completion. Later edits to Z are a new pending operation, not an amendment to an in-flight request hash. |

**Owner-accepted choice:** compare a content revision in addition to content identity. Add `ContentRevision: LibraryCursor` to the item projection and `ExpectedContentRevision` to the content precondition; update it only for content acceptance, never a compatible rename. Keep `ContentVersionId` deterministic from bytes for storage reuse. Scott accepted this behavior after the editing explanation. This changes the Library concurrency contract while preserving deterministic byte identity and storage reuse.

A watcher cannot see unsaved editor memory. If disk X is unchanged while Alice has an unsaved buffer, Grace has no filesystem evidence of that edit. Protecting it needs editor cooperation outside this accepted V1 protocol. For saved changes, recheck disk identity/hash and ownership immediately before a Grace-owned replace. Do not overclaim protection against every uncooperative writer interleaving between a check and an atomic filesystem operation.

**Accepted local schema:** keep the existing shared root exclusion and WDU mechanics; do not add Library as a fourth Branch/Watch/Connect target. Use three Library-owned tables in the later client slice: repository state (including the complete bounded catalog), materialized item ancestry, and durable pending/terminal operations. Fold the `libraries` table into repository state, fetch fresh slot expectations when a local operation needs them instead of persisting a mirror of every vacant server slot, and keep compact conflict provenance on the ordinary conflict item/its pending operation rather than a permanent `library_conflicts` workflow table. This accepted three-table design replaces revision 0.32's six-table design. PR #1044 still implements the older schema and must be adapted.

Keep three kinds of local data: repository progress and participation; each item’s last materialized base; and operations with their frozen source bytes, expected target, server result and recovery details. Once the filesystem change has been checked, one SQLite transaction may update the item and advance the cursor. Save the pending operation before touching files. Keep the predecessor and catalog checks, restart handling and bounded retention of completed operations. If the user edits again during an upload, preserve that edit separately; do not change the operation already submitted.

For an incoming file rename, changed old-path bytes may be removed only when those exact nonempty bytes are already accepted and retained by an existing local operation. Reread that operation and the source immediately before removal. A distinct save at the prepared destination keeps the original item and materialized revision; it does not become an unrelated create. If the server deleted the item before its local edit was submitted, retain the `ItemTombstoned` rejection, saved source and exact request without resurrection. An excluded zero-byte source or target prevents the affected filesystem change and cursor completion.

## Validation matrix

The accepted design uses an executable prototype and focused production tests against the pinned provider and actual serializers. These cases may use a small set of test functions; no general recovery framework or collection of production test interfaces is needed.

| Test area | Concrete cases | What it protects |
| --- | --- | --- |
| Typed provider and ownership | All six key layouts; provider initialization; exact create/replace/clear; fresh activation; failed write with volatile/durable separation; stale ETag; two overlapping activation attempts; no application Cosmos mutation. | Removing record actors does not remove durable-write or ownership guarantees. |
| Acceptance and content | Crash before/after reservation, accepted-change write, tracked add, workflow, item, each slot, receipt, acknowledgement and control completion. Response loss; expired session retry; intervening normal reference/Redis loss; X → Y → X; cleanup generation mismatch. | One accepted operation, exact result, permanent retrievable bytes and no double contribution. |
| Namespace | Nonempty directory rename/cross-Library move; cycle attempt; deep path overflow; case/NFC sibling equality; occupied deterministic conflict name; stale create after vacate; stale catalog removal. | Parent identity and slot generations really replace path copies without losing files. |
| Baseline and SQL | Separate/fresh clients; committed visibility barrier; 100,000 items/tombstones; crash at every shard/manifest boundary; cancellation/response loss; paging touches only necessary shards; repeat baseline builds pause writes. | A snapshot that matches its cursor, bounded work and a measured write pause. |
| History and notification | Tail/segment partial write; sparse history windows; directory ancestor changes; failure before first send; repeated terminal send failure; successful send before failed watermark/clear; restart of fixed fallback. | History reads the stored accepted changes; recovery finds unsent work without probing every old cursor. |
| Client edit/materialization | Alice/Bob scenario with the accepted revision rule; saved edit during remote preparation; second local edit during upload; filesystem success before SQLite failure; terminal before cursor retry; exact Watch echo; directory move. | Receiving server metadata does not replace the local edit base or modified bytes. Stored local progress describes what was actually applied. |
| Contract propagation | Orleans round trips through actual grain calls; stable field IDs; real JSON byte bounds; HTTP/SDK/OpenAPI/TypeScript/Python/Rust regeneration; CLI examples/help; existing authorization and materialization routes. | Deleting internal wrappers does not leave broken serializer or generated client shapes. |

## Requirements and propagation

| ID | Required behavior | Implementation owner and location | Evidence before delivery |
| --- | --- | --- | --- |
| LIB-001 | One repository change order; bounded records; every metadata write uses Orleans. | Issue #1042; Actors decision/record modules and RepositoryLibrary.Actor.fs. | Registered-provider, ETag, duplicate-create, failed-buffer, activation and restart cases. |
| LIB-002 | Exact operation retry and readable retained content precede terminal success. | Issue #1042; acceptance functions and existing upload/counter/workflow actors. | Failure at each meaningful acceptance boundary; lost response; expired upload; cache loss and intervening references. |
| LIB-003 | Parent/name moves preserve identity and reject cycles, occupied conflict names and stale slots. | Issue #1042; shared DTOs, validators and decision functions. | Directory moves, long descendant paths, case/NFC equality, stale create and catalog removal. |
| LIB-004 | Separate content revision detects intervening edits with repeated bytes. | Issues #1042 and #1039; item/precondition DTOs and durable client edit base. | Alice/Bob and X-to-Y-to-X cases, with byte reuse and distinct edit revisions. |
| LIB-005 | Every published baseline matches its committed boundary. | Issue #1042; actor-owned build and Cosmos query functions. | Fresh-client visibility, interrupted builds, manifest-last publication, paging, 100,000-item pause and memory measurements. |
| LIB-006 | History and notifications resume without scanning all lifetime positions. | Issue #1042; cursor segments, tail links, HistoryThrough and NotifyThrough. | Segment/pointer interruption, first-send gap, ambiguous sends and obsolete fallback cleanup. |
| LIB-007 | An issued read URL names a retained accepted version until expiry. | Issue #1042; authorization and token/transfer functions. | Accepted-revision check, later edit/move/delete, permission denial and expiry. |
| LIB-008 | Local cursor advances only after filesystem bytes and SQLite completion. | Issue #1039; Library client modules and three tables. | Process restart around atomic publication/SQLite/cursor; exact Watch echo and changed local bytes. |
| LIB-009 | Library content stays out of version control and WDU completion. | Issue #1039; existing shared exclusion, Watch routing and catalog checks. | Real two-copy round trip, Branch exclusion, zero Save/WDU side effects. |
| LIB-010 | Public, durable and generated contracts change coherently. | Issue #1042 remote surfaces; Issue #1039 local surfaces. | Actual Orleans and JSON round trips, token tests, generation freshness, CLI examples and current-revision Validate. |

Server work updates Types, Shared validation/parameters, Actors, Server handlers/composition, SDK, OpenAPI, generated clients, deployment/provider configuration, documentation and focused tests. Client work updates local configuration, SQLite, CLI, Watch routing, shared exclusion integration, public status and the Windows fixture. Existing WDU row/caller/completion contracts remain unchanged. No new public history route or successful-send outbox is introduced.

## Implementation and next delivery

Issue #1042 implements the remote replacement and keeps provider setup evidence separate from the hosted acceptance, manifest accounting, signed-read and restart tests. The implementation retains useful provider and component work from PR #1043 without adopting its record-actor topology.

Issue #1039 adapts the client to this reviewed server candidate, including the three-table SQLite model and two-copy fixture. Epic #1037 is reassessed after those two dependent changes are composed and validated.

Return to Scott if evidence requires a different actor owner, another durable lifecycle, a changed conflict rule, a new product capability or a material change to delivery scope. Routine field naming, module placement and test plumbing within this design remain implementation choices.
