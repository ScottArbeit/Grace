# Grace

Grace is a version-control system that treats repository state, file identity, and content storage as distinct concepts.
This glossary captures the project language used when discussing Grace's domain model.

## Language

**FileVersion**:
A Grace domain object that says a specific relative path contains specific file content in a repository version.
_Avoid_: File blob, object file, chunked file

**FileContentHash**:
The path-independent BLAKE3 identity of the complete bytes named by a FileVersion, represented as a 64-character
hexadecimal string. A FileContentHash identifies the whole logical file, while ChunkAddress identifies one ContentChunk.
_Avoid_: FileVersion hash, path hash, directory hash

**Version Hash**:
The BLAKE3 or SHA-256 hash value used for version lookup and display. File version hashes are byte-only file content
hashes, DirectoryVersion hashes identify the formal directory preimage, and Reference hashes are the referenced root
DirectoryVersion hashes validated by reference commands. Version hashes stay separate from path-independent CAS
identities such as FileContentHash, ChunkAddress, ContentBlockAddress, and ManifestAddress. BLAKE3 is the default
version-hash algorithm for new version lookup surfaces, and SHA-256 is retained for verification, comparison, lookup
parity, and non-version SHA-256 uses that intentionally stay SHA-256.
_Avoid_: FileContentHash, ChunkAddress, ContentBlockAddress, ManifestAddress

**FileManifest**:
The reconstruction description for large file content that spans ContentChunks stored in ContentBlocks. A FileManifest
references ordered ContentBlockRanges. FileManifest identity comes from reconstruction content, not from the Repository
policy that caused the manifest to be created.
_Avoid_: FileVersion manifest, single-chunk manifest, chunk list

**ManifestAddress**:
The content address of a FileManifest. A ManifestAddress is based on the manifest's reconstruction content. A
FileVersion uses a ManifestAddress when its file content spans multiple ContentChunks.
_Avoid_: Embedded manifest in FileVersion, file blob URI

**ChunkingSuite**:
The fixed, versioned rule set Grace uses to split large file content into ContentChunks. A ChunkingSuite defines the
chunking algorithm, hash algorithm, and boundary rules used by compatible clients and caches.
_Avoid_: Repository chunk settings, per-file chunk tuning

**ContentChunk**:
A path-independent logical piece of large file content identified by a ChunkAddress. ContentChunk is not a standalone
persisted entity, actor, or one-row-per-chunk database record.
_Avoid_: Path chunk, file chunk, chunk actor, chunk row

**KeyChunk**:
A deterministic sparse dedupe lookup anchor derived from a ContentChunk's ChunkAddress and ordinal. A KeyChunk is not a
special kind of ContentChunk and is not stored as durable content state.
_Avoid_: Key chunk entity, globally searchable chunk, dedupe-owned chunk

**ContentBlock**:
The Xorb-like ordered group of ContentChunks that Grace stores, transfers, and garbage-collects as a unit while keeping
each chunk's logical position stable for FileManifest reconstruction. ContentBlock identity comes from the ordered
logical ContentChunk sequence, not from Repository or policy context.
_Avoid_: Xorb, chunk bag, storage blob

**ContentBlockAddress**:
The content address of a ContentBlock. A ContentBlockAddress is based on the block's ordered ChunkAddress values and
compact block format version, not on physical storage layout, per-block storage metadata, per-chunk lengths, or
Repository policy.
_Avoid_: Block path, storage blob URI, repository block id

**ContentBlockRange**:
One ordered reconstruction range inside a FileManifest. A ContentBlockRange points to a contiguous logical ordinal range
inside one ContentBlock and records the range's UncompressedSize.
_Avoid_: FileManifestTerm, xorb term, block pointer

**DedupeCandidateWindow**:
A bounded, response-protected ContentBlock window returned during authorized dedupe discovery so a client can scan for
contiguous reuse runs. A DedupeCandidateWindow is a hint, not a range claim or content-presence authority.
_Avoid_: Chunk existence answer, reusable block proof, raw chunk inventory

**UploadSession**:
A bounded server-authorized workflow for one client's attempt to upload manifest-backed content. An UploadSession
coordinates dedupe discovery, ContentBlock uploads, range claims, and FileManifest finalization.
_Avoid_: FileManifest draft, blob upload token, Save session

**ContentBlockMetadata**:
The mutable storage and lifecycle description for a ContentBlock. ContentBlockMetadata records total and active physical
bytes; reclaimable physical bytes are derived by subtraction. ContentBlockMetadata can change without changing the
ContentBlockAddress.
_Avoid_: ContentBlock identity, payload generation, block format specification

**ContentBlockMetadataRange**:
A physically present logical ordinal range inside ContentBlockMetadata. A ContentBlockMetadataRange with zero active
manifest use is reclaimable; it records the logical ordinal span, active manifest use, and physical byte span. When
Grace compacts it away, Grace removes the range instead of storing a reclaimed flag.
_Avoid_: Reclaimed range tombstone, per-chunk presence record

**CASTarget**:
A StoragePool-shared content-addressed storage object reached from a FileContentReference or FileManifest range that can
receive repository contribution accounting. WholeFileContent is not a CASTarget.
_Avoid_: FileContent, blob URI, path object

**RepositoryContentCounter**:
The repository-scoped ReferenceCount for one StoragePool-shared CASTarget. A RepositoryContentCounter lets many live
DirectoryVersions in one Repository contribute at most one live repository reference to the StoragePool-level CASTarget.
_Avoid_: FileContent actor, global chunk owner, repository storage boundary

**RepositoryReferenceCount**:
The StoragePool-level count on a CASTarget of how many repositories currently use that target. RepositoryReferenceCount
is changed by RepositoryContentCounters, not directly by every DirectoryVersion.
_Avoid_: StoragePool-level ReferenceCount, DirectoryVersion count, global path count

**ManifestContributionWorkflow**:
The separate workflow actor or workflow record that applies or removes one Repository's FileManifest contribution to the
manifest's ContentBlockRanges. A ManifestContributionWorkflow stores its own batch progress, uses the FileManifest's
known ContentBlockRanges, and owns any add/remove workflow status for that manifest contribution.
_Avoid_: Manifest-wide transaction, chunk reverse query, repository counter sharding

**ReferenceCount**:
The live-use count on a DirectoryVersion or RepositoryContentCounter. ReferenceCount records how many live Grace domain
objects directly refer to that object. StoragePool-level CASTargets use RepositoryReferenceCount.
_Avoid_: RetainCount, liveness count, per-root count

**Increment**:
The count change made when a live Grace domain object begins referring to a DirectoryVersion or
RepositoryContentCounter, or when a RepositoryContentCounter adds one repository contribution to a CASTarget's
RepositoryReferenceCount. The canonical operation name is Increment.
_Avoid_: Retain, mark live, IncrementReferenceCount

**Decrement**:
The count change made when a live Grace domain object no longer refers to a DirectoryVersion or
RepositoryContentCounter, or when a RepositoryContentCounter removes one repository contribution from a CASTarget's
RepositoryReferenceCount. The canonical operation name is Decrement.
_Avoid_: Release, unretain, unmark, DecrementReferenceCount

**ReferenceCounted Deletion**:
The normal Grace content-deletion approach where removing a live direct reference Decrements the target count.
StoragePool-wide reachability sweeps are maintenance checks, not the ordinary deletion path.
_Avoid_: StoragePool-wide deletion sweep, sweep-first deletion

**ReferenceCount Deletion Boundary**:
The point where a ReferenceCount or RepositoryReferenceCount reaches zero. At this boundary the object becomes logically
deleted and later becomes physically deleted after Grace's delayed deletion workflow completes.
_Avoid_: Decrement-to-one boundary, immediate physical deletion

**WholeFileContent**:
The content-addressed storage representation for small or regular file content that Grace stores as a whole file instead
of splitting into ContentChunks and ContentBlocks. WholeFileContent is repository-scoped and does not participate in
StoragePool-wide global dedupe.
_Avoid_: SingleChunk, tiny ContentChunk, chunked source file

**FileContentReference**:
The link from a FileVersion to content-addressed storage. A FileContentReference points either to WholeFileContent for
small or regular files or to a FileManifest for large manifest-backed file content.
_Avoid_: BlobUri, always-manifest reference

**ManifestEligibilityPolicy**:
The Repository-level rule set that decides whether a file version stays WholeFileContent or becomes eligible for a
FileManifest. The default ManifestEligibilityPolicy uses Grace's Git-style binary detection, treats binary files as
manifest-backed when their uncompressed size is greater than or equal to 1 MiB, and treats text files as manifest-backed
when their Grace-defined compressed size is greater than or equal to 1 MiB. It may include file-type or extension rules.
_Avoid_: Client chunking preference, global chunking threshold, per-file ChunkingSuite

**ManifestEligibilityDecision**:
The inferred result of applying a Repository's ManifestEligibilityPolicy when a FileVersion is created. Grace infers the
decision from the FileContentReference shape; it does not store a separate decision record.
_Avoid_: Stored eligibility record, dynamic manifest eligibility, retroactive chunking policy

**RecursiveDirectoryVersions**:
The DirectoryVersionDto set returned by calling `GetRecursiveDirectoryVersions()` on a root DirectoryVersion. It is the
existing Grace representation of the directory tree and its FileVersions for a requested Reference or DirectoryVersion.
_Avoid_: ContentPlan, separate restore plan type

**Materialization Plan**:
The server-resolved, immutable plan that names the repository content artifacts a client or cache may materialize for one
authorized DirectoryVersion scope. Grace Server resolves branch names, references, account context, path permissions, and
authorization before issuing or publishing a Materialization Plan.
_Avoid_: Client-resolved checkout plan, cache-resolved branch, raw path request

**Grace Cache**:
A Grace-aware artifact service that stores and serves immutable artifacts named by server-resolved Materialization Plans
near clients or build machines. Grace Cache is an explicit endpoint, and it does not decide repository meaning, branch
resolution, latest/current semantics, account access rules, or path permissions.
_Avoid_: Blob mirror, storage proxy, branch resolver, ACL engine

**Cache Artifact Store**:
The physical Grace Cache storage for immutable artifacts named by Materialization Plans. The V1 required artifacts are a
target-root zip plus recursive metadata for the same DirectoryVersionId.
_Avoid_: Repository chunk folder, per-repo physical cache, baseline-plus-delta store

**Cache Artifact Metadata**:
Grace Cache metadata for artifacts produced from a server-resolved Materialization Plan. Cache Artifact Metadata records
which immutable artifacts are locally present and eligible for retention; it is not repository authority.
_Avoid_: Chunk presence, branch state, path-scoped access decision

**Cache Retention**:
The single retention period for locally held Materialization Plan artifacts. Prefetches and successful reads can refresh
Cache Retention, but they do not create separate retention classes for the same cached artifact set.
_Avoid_: Per-chunk retention, separate prefetch/read retention, pinned cache entries

**ContentAccessGrant**:
A short-lived, server-issued authorization that Grace Cache can validate without calling Grace Server. A
ContentAccessGrant scopes a cache request to server-resolved Grace content, such as a Materialization Plan,
DirectoryVersionId, target-root zip, or recursive metadata artifact.
_Avoid_: Delegated content capability, raw artifact token, hash authorization

**Resolved Content Scope**:
The immutable Grace object scope named by a ContentAccessGrant, such as a ReferenceId or DirectoryVersionId. Branch names
may be request context, but they are not grant scope.
_Avoid_: Branch-name grant, moving grant scope

**Resolved Path Scope**:
The path limits Grace Server places in a ContentAccessGrant after evaluating authentication claims, RBAC roles, and path
permissions. Grace Cache enforces Resolved Path Scope but does not decide it.
_Avoid_: Cache ACL decision, raw path request

**Grant Binding**:
The requester identity and cache audience that a ContentAccessGrant is issued for. Grant Binding prevents a grant from
being treated as a reusable bearer secret.
_Avoid_: Bearer-only cache token, unbound access token

**Cache Service Identity**:
The registered identity Grace Cache uses for configured prefetch subscriptions. Cache Service Identity does not by
itself authorize serving cached artifacts to arbitrary callers.
_Avoid_: Global artifact reader, mirror credential

**Authorization Scope**:
A node in Grace's authorization hierarchy where RoleAssignments can be granted, such as System, Owner, Organization,
Repository, or Branch. Repository-contained objects inherit access through an Authorization Scope instead of becoming
separate scopes, and a DirectoryVersion is repository-contained rather than its own Authorization Scope.
_Avoid_: Account, entity ACL, object owner

**Role Inheritance**:
The downward application of RoleAssignments from an ancestor Authorization Scope to its descendants. Role Inheritance is
additive: a narrower-scope assignment can add effective permissions, but it does not subtract inherited permissions.
_Avoid_: Permission copy, cascading ACL rows, down-scope deny

**Effective Permission**:
The permission Grace derives for a principal by combining matching RoleAssignments across the target Authorization Scope
and its ancestors, plus any applicable path permissions. Effective Permission describes the current authorization
answer, not a stored role assignment. Effective Permission names use full scope names, such as OrganizationWrite or
RepositoryRead, rather than abbreviations; SystemOperate is the system-level support operation permission.
_Avoid_: Direct permission, computed role, owner flag, Org permission, Repo permission

**Scope Role**:
A named authorization role granted at an Authorization Scope. Scope Role names use full scope names, such as
OrganizationAdmin or RepositoryContributor, rather than abbreviations; Admin remains the canonical administrative
suffix. Contributor is the write-level suffix for Owner, Organization, and Repository scopes; Writer is the write-level
suffix for Branch.
_Avoid_: Org role, Repo role, short role ID, Administrator suffix

**Administrative Scope Role**:
A Scope Role that grants administrative Effective Permissions at its scope and descendant scopes, including
authorization-management authority. Write-level and reader Scope Roles are not Administrative Scope Roles.
_Avoid_: Local-only admin, grant-only role, contributor administrator

**SystemOperator**:
A system-scope support role with system-wide write and administrative Effective Permissions for operating Grace while
excluding SystemAdmin-level authority. SystemOperator can administer descendant scopes but cannot manage system-scope
identity, authorization, bootstrap, or security policy; SystemOperate is its system-level permission.
_Avoid_: Owner creator only, support superuser, hidden SystemAdmin, system identity administrator

**SystemReader**:
A system-scope support role with system-wide read Effective Permissions and no write or administrative Effective
Permissions. SystemReader is the role name; SystemRead is the permission name.
_Avoid_: SystemRead role, read-only admin, support operator

**Write-Level Scope Role**:
A Scope Role that grants read/write Effective Permissions at its scope and covered descendant scopes without granting
admin Effective Permissions. OwnerContributor, OrganizationContributor, RepositoryContributor, and BranchWriter are
write-level roles.
_Avoid_: Contributor admin, implicit administrator, write admin

**Scope Creator Admin Grant**:
The RoleAssignment Grace creates when a new Authorization Scope would otherwise leave the authenticated creator without
the matching Effective Permission. A Scope Creator Admin Grant is not duplicated when Role Inheritance already gives the
creator admin rights to the new scope, and it belongs to the creator's User principal rather than the credential or
group used to authenticate. It is not added when the requested Authorization Scope already exists.
_Avoid_: Created-by privilege, implicit ownership, redundant child grant, creator bypass, token-owned grant, group
promotion, existing-scope repair

**Scope Creation Authorization**:
The authorization rule for creating Authorization Scopes. Creating a child Authorization Scope requires admin or write
Effective Permission on the containing parent scope, while creating a top-level Owner requires system administrative or
system operate Effective Permission. Commands that create repository-contained or branch-contained objects are not Scope
Creation Authorization even when their route names include `create`; creating a Branch Authorization Scope is scope
creation, but creating a Branch Reference is not.
_Avoid_: Self-appointed child admin, create-anywhere permission, child-scope bootstrap, create-route authorization,
reference creation, directory-version creation

**Scope Creation Success**:
The completed outcome of creating an Authorization Scope where the new scope exists and the authenticated creator has
matching administrative Effective Permission. A response that leaves the creator without that permission is not Scope
Creation Success.
_Avoid_: Stranded scope, partial create, success without admin

**Non-Scope Creation**:
The creation of an object contained by an Authorization Scope rather than a new Authorization Scope. Non-Scope Creation
requires the containing scope's relevant write or administrative Effective Permission and does not create a Scope Creator
Admin Grant.
_Avoid_: Scope bootstrap, implicit admin grant, contained scope creation

**Cache Mode**:
The operating posture for Grace Cache. CI Cache Mode serves controlled build infrastructure, while User Cache Mode
serves normal Grace clients; both modes use ContentAccessGrants for per-call validation.
_Avoid_: Bypass mode, unauthenticated cache

**ChunkAddress**:
The canonical BLAKE3 hash of a ContentChunk's unencoded bytes, represented as a 64-character hexadecimal string. A
ChunkAddress names content, not a storage path, storage encoding, or algorithm-prefixed multihash.
_Avoid_: Chunk file name, blob path, prefixed chunk name, algorithm-prefixed chunk id

**Content-Addressable Storage**:
The storage layer that stores and retrieves file content through FileContentReferences, WholeFileContent,
FileManifests, ContentBlocks, and ContentChunks without making the repository path part of the storage identity.
_Avoid_: Path-aware object storage, per-file blob naming

**StoragePool**:
The durable dedupe, placement, lifecycle, billing, and compliance boundary for repository content. A Repository maps to
a StoragePool rather than directly to a cloud storage account.
_Avoid_: Repository storage account, global storage account

**Organization StoragePool**:
A StoragePool owned by one Organization, commonly used for BYOS or organization-isolated storage.
_Avoid_: Shared hosted pool

**Hosted Shared StoragePool**:
A provider-owned StoragePool that can serve multiple Organizations while keeping logical billing and authorization
scoped per Repository or Organization.
_Avoid_: Organization-owned pool, visible cross-tenant dedupe

**UsageFact**:
An immutable measurement or declaration of Grace resource usage for one accounting-relevant occurrence. A UsageFact is
the raw source material for customer usage aggregates and operator cost reconciliation.
_Avoid_: UsageEvent, UsageOperation, raw ledger entry

**UsageFactId**:
The stable identity Grace uses to recognize one UsageFact across retries, replay, and ingestion. A UsageFactId is the
UsageFact uniqueness identity, while a CorrelationId provides request or workflow lineage.
_Avoid_: CorrelationId-as-usage-id, usage operation id, retry counter

**Customer Usage Ledger**:
The customer-visible record of Grace usage counts and proof derived from UsageFacts and repository reference state.
Customer Usage Ledger entries describe Grace usage, not provider invoices or Grace's supplier costs.
_Avoid_: Azure cost report, customer invoice line source, provider billing ledger

**Operator Cost Ledger**:
The internal Grace record that reconciles provider costs and resource usage with Grace usage. The Operator Cost Ledger
supports margin, anomaly, and capacity analysis and is not customer-facing proof.
_Avoid_: Customer usage proof, customer-visible Azure bill, repository invoice ledger

**Charge Ledger**:
The record of Grace pricing policy applied to Customer Usage Ledger counts. The Charge Ledger contains billable Grace
charges, not raw provider cost, physical dedupe savings, or unpriced usage facts.
_Avoid_: Pricing policy, raw usage ledger, operator cost ledger

**Repository Storage**:
The customer-facing usage category for storage consumed by a Repository's logical retained content. Repository Storage
can include active and recoverable deleted content according to Grace retention policy.
_Avoid_: Blob storage, physical dedupe storage, soft-deleted storage line item

**StorageShard**:
A physical storage resource inside a StoragePool, such as an Azure Storage account/container or S3 bucket/prefix group.
A StoragePool may contain many StorageShards.
_Avoid_: Dedupe boundary, repository storage setting

**StoragePool Migration**:
The operational process of moving a Repository from one StoragePool to another while Grace continues serving repository
traffic.
_Avoid_: Offline storage move, account rename

**StoragePoolMigration**:
The operational record for one StoragePool Migration, including source and target pools, state, progress, verification,
retry, and audit details.
_Avoid_: Repository storage fields, one-off migration flags

**StoragePool Source Fallback**:
The read behavior during StoragePool Migration where Grace reads from the target StoragePool first, then falls back to
the source StoragePool for content that has not been copied yet.
_Avoid_: Dual-write migration, source-primary migration

**Webhook**:
A post-event outbound HTTP notification rule for committed Grace facts. A Webhook includes its URL and is not an
approval gate or a reusable destination object.
_Avoid_: Hook, subscription, notification rule, webhook destination

**Webhook Delivery**:
The durable runtime delivery work for sending one Webhook notification after a committed Grace fact. Webhook Delivery is
performed outside domain actors so external HTTP failures cannot block or roll back the source workflow.
_Avoid_: Actor callback, source workflow step, synchronous event handler, approval notification delivery

**External Webhook Event**:
A stable public event contract that can trigger Webhook Delivery. External Webhook Events are mapped from selected
committed Grace facts; they are not raw internal automation event names.
_Avoid_: AutomationEventType, F# union case, SignalR event

**Approval Policy**:
A pre-transition gate that defines when a protected Grace operation requires approval. An Approval Policy creates or
finds Approval Requests; it is not itself the runtime decision.
_Avoid_: Webhook approval, hook policy, validation requirement

**Approval Request**:
A durable runtime yes/no decision created by Grace for one protected operation scope. Approval state belongs to the
Approval Request, not to the PromotionSet lifecycle status.
_Avoid_: Manual approval ticket, validation result, synchronous webhook response

**Approval Notification Delivery**:
The delivery and audit record for notifying an Approval Responder about an Approval Request. Approval Notification
Delivery is not a Webhook Delivery, even when both use the same outbound HTTP machinery.
_Avoid_: Webhook delivery, approval webhook, webhook rule

**Unsafe Local Outbound Target**:
An explicitly marked development-only outbound HTTP target for testing Webhooks or Approval Notification Delivery
against a loopback URL. It is not a normal public notification target and must remain visible as unsafe local behavior
in product surfaces.
_Avoid_: Production webhook URL, private-network target, public callback endpoint

**Approval Responder**:
A principal or role selected by an Approval Policy as allowed to answer an Approval Request. Being selected as a
responder is necessary but not sufficient; the responder must also have authority over the protected scope.
_Avoid_: Approver-only permission, release-manager string, webhook callback identity

**Promotion Set Approval Summary**:
The approval readiness information Grace can show beside a PromotionSet, such as pending, approved, rejected, or
expired approval state for the current apply scope. It is derived from Approval Requests and is not the persisted
PromotionSet lifecycle status.
_Avoid_: PromotionSet pending status, blocked status, compute status

## Flagged Ambiguities

**File identity vs. storage identity**:
Grace uses FileVersion for path-aware repository meaning, and Content-Addressable Storage for path-independent storage
identity. Do not describe chunks as belonging to a path; chunks belong to content-addressed storage.

**Version graph hash vs. CAS address**:
ADR 0006 uses version hashes for version lookup and display while preserving the current hash inputs for each object
kind: file byte streams for FileVersion, the formal directory preimage for DirectoryVersion, and the referenced root
DirectoryVersion hashes for Reference. Grace uses FileContentHash, ChunkAddress, ContentBlockAddress, and
ManifestAddress for content-addressed storage identity. Do not use a CAS address as a version hash, and do not use a
version hash as a chunk, block, manifest, or file-content address.

**File content vs. chunk identity**:
Grace uses FileContentHash for the complete file bytes and ChunkAddress for one ContentChunk inside manifest-backed
large file content. Do not use ChunkAddress to mean the whole file.

**Repository count vs. StoragePool count**:
Grace uses RepositoryContentCounter to absorb repository-local fan-in for common CASTargets. The StoragePool-level
RepositoryReferenceCount counts live repository contributions, not every DirectoryVersion that can reach the target.

**Manifest contribution vs. chunk reverse lookup**:
Grace uses ManifestContributionWorkflow to update ContentBlock range liveness from the FileManifest's known
ContentBlockRanges. It does not query for which manifests, DirectoryVersions, or repositories mention a chunk during
ordinary deletion.

**Active workflow vs. global delete block**:
An active ManifestContributionWorkflow does not block all ContentBlock compaction or deletion globally. It can block
physical deletion of the FileManifest whose block contribution workflow is still in progress. ContentBlock cleanup is
decided from the block's own active logical chunk ranges and delayed-reminder checks, not by a global workflow scan.

**Repository counter vs. pending contribution state**:
RepositoryContentCounter does not store ContributionState. When its ReferenceCount crosses zero, the counter calls
Increment or Decrement on the CASTarget in the same Orleans transaction. ManifestContributionWorkflow owns manifest
batch progress separately.

**Workflow progress vs. FileManifest state**:
Grace stores ManifestContributionWorkflow batch progress in the workflow actor or record, not inside the FileManifest
actor. FileManifest may track or deterministically locate workflow ids, but it does not own per-repository batch state.

**Reference object vs. ReferenceCount**:
Grace uses Reference for saved repository roots such as saves, checkpoints, commits, and promotions. ReferenceCount is
not limited to counting Reference records; it counts direct live references between Grace domain objects.

**Normal deletion vs. maintenance sweep**:
Grace uses ReferenceCounted Deletion as the normal cleanup path after References expire or otherwise stop keeping content
live. Reachability sweeps exist to verify and repair, not to make routine deletions possible.

**Zero count vs. physical deletion**:
ReferenceCount reaching zero makes content eligible for deletion, but physical deletion happens later. Grace does not use
ReferenceCount one as a deletion boundary.

**Chunk address vs. physical layout**:
Grace uses ChunkAddress as the canonical name for a ContentChunk. Storage providers and local caches may choose
different physical layouts without changing the ChunkAddress.

**Chunk identity vs. chunk entity**:
Grace uses ContentChunk to name a logical chunk of content, not a persistent object created for every chunk. Chunk-scale
metadata belongs in block indexes, manifests, caches, or dedupe indexes that batch many chunks together.

**Whole file vs. manifest**:
Grace uses WholeFileContent for small or regular files and FileManifest only for large manifest-backed files.
FileVersions do not point directly at ChunkAddresses.

**Repository-scoped whole files vs. global dedupe**:
WholeFileContent is content-addressed inside one Repository, but it is not shared through StoragePool-wide global dedupe.
Global dedupe belongs to large manifest-backed files through FileManifests, ContentBlocks, and ContentChunks.

**Fixed suite vs. local tuning**:
Grace uses ManifestEligibilityPolicy to decide whether a file should enter the manifest-backed path, but once it does,
Grace uses a fixed ChunkingSuite for compatible CAS storage and cache reuse. Repositories do not define their own
chunking parameters.

**Chunk target vs. manifest eligibility threshold**:
Grace's v1 ChunkingSuite uses Xet-sized chunks: 64 KiB target, 8 KiB minimum, and 128 KiB maximum. The
ManifestEligibilityPolicy threshold decides whether a file enters the manifest-backed path; it does not make chunks
larger or smaller.

**Repository policy vs. global default**:
ManifestEligibilityPolicy belongs to a Repository. Grace can provide defaults, but the repository owns the effective
thresholds and include or exclude rules for deciding WholeFileContent vs. FileManifest.

**Binary size vs. text compressed size**:
Grace's default ManifestEligibilityPolicy uses uncompressed size for binary files and Grace-defined compressed size for
text files. Binary/text classification uses Grace's Git-style binary detection.

**Policy evaluation vs. content immutability**:
Grace applies ManifestEligibilityPolicy when creating a FileVersion. The resulting FileContentReference is immutable and
is the source of truth for whether content is whole-file or manifest-backed; later policy changes do not reinterpret old
FileVersions.

**Manifest identity vs. repository policy**:
ManifestEligibilityPolicy can decide whether a Repository creates a FileManifest, but policy context is not part of
FileManifest identity. The same reconstruction content can reuse the same ManifestAddress across repositories and policy
versions.

**Manifest identity vs. range size**:
ManifestAddress includes the reconstruction ranges' UncompressedSize values because those sizes are part of the
reconstruction contract Grace validates.

**Block identity vs. repository policy**:
ContentBlock identity is based on the ordered ChunkAddress sequence and compact block format version. Repository policy
can decide whether file content enters the block path, but it does not make otherwise identical ContentBlocks distinct.

**Block format vs. block storage metadata**:
Grace stores a compact block format version with ContentBlock metadata to select the fixed ContentBlock interpretation
rules. The full format specification is defined by Grace itself and is not duplicated inside each ContentBlock.

**Block identity vs. block metadata**:
ContentBlockAddress does not include per-chunk lengths. Encoded offsets, encoded lengths, compression details,
lifecycle state, and optional unencoded-length checks describe storage or accounting around a ContentBlock, not the
block's current identity.

**Block compaction vs. block identity**:
ContentBlock compaction can rewrite physical payload placement while preserving the ContentBlockAddress and logical
ChunkAddress ordinals. Grace does not need a separate domain identity for each compacted physical layout.

**Dedupe hint vs. physical presence**:
Global dedupe results are hints, not authority. Grace must confirm a ContentBlockMetadataRange is physically present and
make the range active before a FileManifest can rely on it.

**Global dedupe lookup vs. per-chunk existence**:
Grace queries sparse key ChunkAddresses and receives candidate ContentBlock windows to scan for contiguous matching runs.
Grace does not ask the global dedupe index whether every chunk exists, and it does not create durable ContentBlockRanges
from isolated chunk matches that would fragment reconstruction.

**KeyChunk vs. durable content state**:
A KeyChunk is a derived lookup anchor used to find candidate windows. It does not change ContentChunk identity, liveness,
reference counting, or storage placement.

**Dedupe discovery vs. existence oracle**:
Grace uses authorized dedupe discovery to find candidate ContentBlock windows, not to answer whether an arbitrary
ChunkAddress exists. A dedupe miss, stale hint, policy miss, authorization miss, or rate-limit response should not be
treated as a StoragePool-wide content-presence fact.

**Upload session vs. durable manifest**:
An UploadSession is temporary coordination state. The durable content identity is the accepted FileManifest and the
ContentBlocks it references, not the upload attempt that produced them.

**Upload session retention vs. content retention**:
Deleting UploadSession actor state after its retention period deletes upload coordination evidence only. It does not
delete accepted FileManifests, ContentBlocks, ContentBlockMetadata, or repository contribution accounting.

**Client manifest proposal vs. FileManifest authority**:
A client may propose a FileManifest from locally chunked bytes, uploaded ContentBlocks, and candidate reuse ranges. Grace
Server is authoritative for accepting range claims, validating reconstruction, and finalizing the durable FileManifest.

**Content identity vs. storage encoding**:
Grace computes ChunkAddress from a ContentChunk's unencoded bytes. Compression is a storage or transfer encoding, not
part of chunk identity.

**Server authority vs. cache artifacts**:
Grace Server is authoritative for resolving repository meaning, account access, references, branch inputs, path
permissions, and the DirectoryVersionId behind a Materialization Plan. Grace Cache is authoritative only for which
immutable plan artifacts it already has locally.

**Explicit cache vs. storage interposition**:
Grace Cache is used intentionally by Grace-aware clients and CI workers. It does not intercept object-storage traffic or
pretend to be the backing storage account.

**Possession vs. permission**:
Grace Cache may store target-root zips and recursive metadata before a requester needs them, but it serves artifacts only
when the requester has a current server-issued grant that authorizes the whole target-root scope for the resolved
Materialization Plan or DirectoryVersionId. DirectoryVersionId equality alone is not enough for path-narrowed grants:
V1 full-root artifacts must reject narrowed path-scope grants until Grace accepts path-scoped artifact shapes and their
recursive metadata contract.

**Private subnet vs. authorization**:
A private subnet reduces exposure for Grace Cache, but it does not replace per-call authorization. Grace Cache validates
every request and checks the requested content against the requester's ContentAccessGrant.

**Server issuance vs. local validation**:
Grace Server issues ContentAccessGrants, and Grace Cache validates them locally without calling Grace Server on every
cache request.

**Bound grant vs. bearer secret**:
Grace Cache validates a ContentAccessGrant against the caller and cache audience. Possession of an unbound token is not
enough to fetch cache artifacts.

**Grant scope vs. materialization data**:
Grace Cache trusts a valid ContentAccessGrant for its declared scope. When a grant authorizes a Materialization Plan,
Grace Cache serves only the artifacts named by that server-resolved plan.

**Resolved scope vs. moving names**:
Grace Server resolves branch names and latest/current-style inputs before issuing a ContentAccessGrant. The grant
authorizes immutable plan artifacts for a resolved DirectoryVersionId, not a moving name that can change after issuance.

**Authorization decision vs. cache enforcement**:
Grace Server evaluates claims, roles, and directory or file path permissions before issuing a ContentAccessGrant. Grace
Cache enforces the resolved grant scope against Materialization Plan artifacts, but it does not evaluate ACL policy.

**Artifact storage vs. scoped access**:
Grace Cache stores immutable Materialization Plan artifacts in a Cache Artifact Store, but serves them only through
repository-, DirectoryVersion-, path-, and caller-scoped authorization resolved by Grace Server.

**Cache metadata vs. artifact presence**:
Cache artifacts remain live only while unexpired Cache Artifact Metadata refers to them. Physical presence in the Cache
Artifact Store is not repository authority or retention authority.

**Cache expiry vs. pinning**:
Grace Cache uses expiry-based retention for cached Materialization Plan artifacts. It does not pin cached content in v1.

**Repository storage vs. physical storage**:
A Repository maps to a StoragePool. Storage accounts, containers, buckets, and prefixes are StorageShards inside that
pool and may change as the pool scales.

**Pool ownership vs. repository ownership**:
Some StoragePools are owned by one Organization; hosted shared StoragePools are provider-owned and can serve multiple
Organizations without changing repository ownership.

**Migration writes vs. migration reads**:
During StoragePool Migration, new content writes go to the target StoragePool. Reads use StoragePool Source Fallback
until reachable content has been copied and verified in the target.

**Repository routing vs. migration progress**:
Repository state carries only the active storage routing summary needed for reads and writes. StoragePoolMigration holds
the operational progress and audit history.

**PromotionSet status vs. approval state**:
PromotionSet status describes the PromotionSet lifecycle. Approval state describes the Approval Request that gates a
protected operation, though list views may show a derived Promotion Set Approval Summary for usability.

**Internal automation event vs. external webhook event**:
Grace may have multiple internal automation signals for one workflow fact. A Webhook must use the External Webhook Event
registry so one public event name has one canonical source or an explicit dedupe rule.

**Public notification target vs. unsafe local target**:
Normal Webhook and Approval Notification Delivery URLs target public HTTPS endpoints. Loopback targets are only for
explicit local development testing and should be treated as unsafe local behavior, not as production notification
configuration.

## Example Dialogue

Developer: "If `src/app.fs` and `backup/app.fs` contain the same bytes, are they the same FileVersion?"

Domain expert: "No. They are two FileVersions because their relative paths differ, but they may refer to the same
FileContentHash and FileContentReference."

Developer: "Can I use FileContentHash as the FileVersion hash?"

Domain expert: "No. FileContentHash is a path-independent content identity. A FileVersion version hash identifies the
version graph object that says a specific relative path contains specific content."

Developer: "Can the cache serve materialized content without resolving the original branch or path request?"

Domain expert: "Yes. Grace Server resolves the Materialization Plan first. Grace Cache can then serve the immutable
artifacts named by that plan without making repository or path-permission decisions."

Developer: "If a file has several chunks, which hash identifies the whole file?"

Domain expert: "Use FileContentHash for the complete file and ChunkAddress for each individual chunk."

Developer: "Is ReferenceCount the number of Save References that can reach a chunk?"

Domain expert: "No. ReferenceCount counts direct live references between Grace domain objects, such as a DirectoryVersion
referring to a child DirectoryVersion, WholeFileContent, or FileManifest."

Developer: "If a common React `.js` file appears in thousands of DirectoryVersions, should Grace globally chunk-dedupe
that source file?"

Domain expert: "No. Small or regular code files use WholeFileContent. Chunk and ContentBlock dedupe is for large
manifest-backed files."

Developer: "Can two repositories globally share the same small WholeFileContent object?"

Domain expert: "No. WholeFileContent is repository-scoped. StoragePool-wide global dedupe is for large manifest-backed
files, not small or regular files."

Developer: "When a Save Reference expires, do we retain and release objects?"

Domain expert: "Use Increment and Decrement. A live reference increments the object it points to; when that direct
reference is removed, Grace decrements the target."

Developer: "Does Grace need a StoragePool-wide sweep before it deletes expired Save content?"

Domain expert: "No. ReferenceCounted Deletion is the normal cleanup path. Sweeps are maintenance checks for repair and
confidence."

Developer: "When ReferenceCount reaches one, should Grace prove there is only one remaining reference?"

Domain expert: "No. ReferenceCount one is useful telemetry. ReferenceCount zero is the deletion boundary."

Developer: "If the local cache stores a chunk under a fanout directory, is that directory part of the chunk's identity?"

Domain expert: "No. The ChunkAddress is the chunk's identity; the local path is only a physical layout choice."

Developer: "Should every file have a FileManifest?"

Domain expert: "No. Small or regular files use WholeFileContent. A FileManifest is only needed for large
manifest-backed file content."

Developer: "Should Grace update thousands of chunk ranges for a large FileManifest in one transaction?"

Domain expert: "No. Use a ManifestContributionWorkflow that applies or removes the repository's contribution to
ContentBlock ranges in bounded batches."

Developer: "Should ManifestContributionWorkflow progress live inside the FileManifest actor?"

Domain expert: "No. ManifestContributionWorkflow is a separate workflow actor or record. FileManifest can track or
deterministically locate the workflow ids it created, but the workflow owns batch progress."

Developer: "Does one active ManifestContributionWorkflow block all ContentBlock cleanup globally?"

Domain expert: "No. It can block physical deletion of the FileManifest whose block contribution workflow is in progress.
ContentBlock cleanup is decided by the block's active logical chunk ranges and delayed-reminder checks, not by a
StoragePool-wide workflow scan."

Developer: "Does RepositoryContentCounter need ContributionState?"

Domain expert: "No. RepositoryContentCounter stores ReferenceCount and lifecycle state. It calls Increment or Decrement
on the CASTarget when its ReferenceCount crosses zero. ManifestContributionWorkflow owns manifest batch progress."

Developer: "Does Grace need a FileContent actor between FileVersion and chunks?"

Domain expert: "No. FileVersion carries a FileContentReference, which points either to WholeFileContent for small or
regular files or to a FileManifest for large manifest-backed file content."

Developer: "Who decides whether a file is stored whole or manifest-backed?"

Domain expert: "The Repository's ManifestEligibilityPolicy. The default policy uses Grace's Git-style binary detection:
binary files enter the manifest-backed path at 1 MiB uncompressed, and text files enter it at 1 MiB compressed. The
policy can later include file extension or file-type rules."

Developer: "If the repository changes ManifestEligibilityPolicy, do old FileVersions change from WholeFileContent to
FileManifest?"

Domain expert: "No. ManifestEligibilityPolicy is evaluated when a FileVersion is created. The resulting
FileContentReference stays immutable."

Developer: "Does Grace store a ManifestEligibilityDecision separately from FileContentReference?"

Domain expert: "No. Grace infers the decision from whether FileContentReference points to WholeFileContent or
FileManifest."

Developer: "If a repository changes its ManifestEligibilityPolicy, does it change the chunking algorithm?"

Domain expert: "No. ManifestEligibilityPolicy decides whether to use a FileManifest. ChunkingSuite defines how eligible
large files are chunked."

Developer: "Is repository policy part of a FileManifest's identity?"

Domain expert: "No. Policy decides whether to create a FileManifest. ManifestAddress comes from reconstruction content,
so identical manifest content can be reused across repositories and policy versions."

Developer: "Is repository policy part of a ContentBlock's identity?"

Domain expert: "No. ContentBlockAddress comes from the ordered ChunkAddress sequence and compact block format version,
so identical ContentBlocks can be reused across repositories and policy versions."

Developer: "Can one repository choose smaller chunks than another repository?"

Domain expert: "No. Compatible Grace clients and caches use the same ChunkingSuite so ContentChunks can be reused."

Developer: "Should Grace make chunks larger because ContentBlocks are large?"

Domain expert: "No. Grace keeps ContentChunks Xet-sized and uses ContentBlocks for large storage and transfer units.
The larger repository-specific threshold belongs to ManifestEligibilityPolicy, not ChunkingSuite."

Developer: "If a chunk is compressed before upload, is the compressed byte stream what gets hashed?"

Domain expert: "No. The ChunkAddress is computed from the unencoded chunk bytes."

Developer: "Can a cache decide what chunks belong to a version by scanning local storage?"

Domain expert: "No. Grace Server resolves the Materialization Plan. Grace Cache only decides whether the plan's
immutable artifacts are present locally."

Developer: "Is RecursiveDirectoryVersions a new CAS type?"

Domain expert: "No. It is the existing result of calling `GetRecursiveDirectoryVersions()` on a root DirectoryVersion."

Developer: "Does Grace Cache make downloads faster by pretending to be Azure Blob Storage?"

Domain expert: "No. Grace-aware clients call Grace Cache explicitly for artifacts named by a server-resolved
Materialization Plan."

Developer: "If Grace Cache already has a target-root zip, can anyone on the subnet fetch it by hash?"

Domain expert: "No. The requester needs a server-issued grant for the resolved Materialization Plan or
DirectoryVersionId that names the artifact and authorizes the whole target-root scope. Matching the DirectoryVersionId
is not enough when the grant is narrowed to a path; V1 full-root artifacts must reject narrowed path-scope grants until
Grace accepts path-scoped artifact shapes."

Developer: "If the cache runs on a private CI subnet, can it skip authorization checks?"

Domain expert: "No. The subnet reduces exposure, but Grace Cache still validates every call for security logging,
telemetry, and scoped access."

Developer: "Does Grace Cache need to call Grace Server before serving every request?"

Domain expert: "No. Grace Server issues the ContentAccessGrant, and Grace Cache validates that grant locally."

Developer: "If someone copies a cache token from a CI log, can they use it from another machine?"

Domain expert: "No. A ContentAccessGrant is bound to the requester identity and cache audience."

Developer: "Does a grant for a Materialization Plan need to include a separate hash of the recursive metadata?"

Domain expert: "No. The valid grant authorizes the server-resolved plan. In v1, the plan names a target-root zip and
recursive metadata for the same DirectoryVersionId."

Developer: "Can a grant say 'whatever main means right now'?"

Domain expert: "No. Grace Server resolves `main` first, then issues the grant for immutable artifacts tied to the
resolved DirectoryVersionId."

Developer: "Does Grace Cache evaluate claim-based directory or file ACLs?"

Domain expert: "No. Grace Server evaluates ACL policy and issues the grant; Grace Cache enforces the resolved scope."

Developer: "If two repositories materialize the same DirectoryVersionId, does Grace Cache own the access decision?"

Domain expert: "No. Grace Cache can store immutable artifacts, but Grace Server owns repository meaning, account access,
path permissions, and the Materialization Plan."

Developer: "Does Grace Cache keep artifacts alive just because they exist on disk?"

Domain expert: "No. Cache Artifact Metadata controls local retention; artifacts with no unexpired metadata can be
deleted."

Developer: "Can an operator pin a cached Reference so GC never removes it?"

Domain expert: "No. Grace Cache v1 uses expiry-based retention, not pinning."

Developer: "Does a Repository own one Azure Storage account?"

Domain expert: "No. A Repository maps to a StoragePool; the pool owns whatever StorageShards are needed."

Developer: "Can Grace move a Repository to a different StoragePool later?"

Domain expert: "Yes. StoragePool Migration moves repository content between pools while traffic continues."

Developer: "During StoragePool Migration, does Grace write new chunks to both pools?"

Domain expert: "No. New writes go to the target StoragePool, while reads can fall back to the source StoragePool."

Developer: "Does the Repository record hold all migration progress?"

Domain expert: "No. The Repository holds routing state; StoragePoolMigration holds progress and audit history."

Developer: "If applying a PromotionSet is waiting for an Approval Responder, is the PromotionSet blocked?"

Domain expert: "No. The Approval Request is pending. The PromotionSet can remain ready while list views show the
derived Promotion Set Approval Summary."

## Branch Annotation Context

This context captures the project language for the Grace branch annotation design discussion. It is a glossary only, not
an implementation specification.

### Branch Annotation Language

**Branch annotation**:
A view of a file on a Branch that explains which References account for the file's visible lines. It is the Grace
feature concept behind `grace branch annotate`.
_Avoid_: Blame, origin search

**Line source**:
The Reference that explains a visible line under the selected branch annotation rules. A line source may identify when
the line last changed, when it became visible on the Branch, or both, but it is always expressed in Grace terms as a
Reference.
_Avoid_: Git origin, distributed origin

**Reference creator**:
The identity recorded for the event that created a Reference. It is the preferred language for "who created this
Reference" in branch annotation output.
_Avoid_: Author, committer, Git user

**Effective branch history**:
The References needed to explain the content visible on a Branch, including relevant `BasedOn` relationships when a
branch was created or rebased. One Branch has one effective branch history for a selected Target Reference, subject to
the chosen annotation filters.
_Avoid_: Origin history, remote history

**Annotation line**:
A 1-based visible text line in the target file after line-ending normalization for annotation comparison. Terminal
newlines do not create an additional visible line. Annotation lines apply only to text files; binary files are outside
branch annotation V1.
_Avoid_: Diff line, physical newline record

**Annotation boundary**:
A point where branch annotation can still return visible target lines, but cannot fully prove one or more line source
details under the selected annotation rules. Boundaries must be explicit and must not be presented as complete source
attribution.
_Avoid_: Silent fallback, unknown origin

**Last changed Reference**:
The Reference where a visible annotation line or line span last became different at the annotated RelativePath under the
selected branch annotation rules. A Rebase Reference is a Last changed Reference only for lines whose visible content
changed at that Rebase. Human branch annotation output defaults to this line source role.
_Avoid_: Last changed by, committer, author

**Introduced Reference**:
The earliest Reference in effective branch history where a visible annotation line or line span is known, through
annotation line continuity, to appear at the annotated RelativePath under the same line comparison rules used by branch
annotation. It may be unknown when annotation reaches a boundary before proving the earliest same-path appearance.
_Avoid_: Origin Reference, original author, first commit, earliest text match

**Annotation span**:
A contiguous range of annotation lines in the target file that share the same resolved line source details under the
selected branch annotation rules. Branch annotation JSON should represent repeated line attribution as spans rather than
duplicating one source object per line.
_Avoid_: Blame hunk, diff hunk, repeated per-line record

**Exact path annotation**:
Branch annotation that explains visible lines only at the selected RelativePath. It does not follow renames, copies, or
moved blocks across other paths. In V1, a delete-plus-add or rename-like history is a path boundary unless the same
RelativePath remains visible in effective branch history.
_Avoid_: Rename following, copy detection, repository-wide provenance

**Target Reference**:
The Reference whose directory and file state branch annotation is explaining. If no Target Reference is selected
explicitly, `grace branch annotate` uses the selected Branch's latest Reference.
_Avoid_: HEAD, tip, origin

**Annotation line continuity**:
The relationship established by branch annotation when a target annotation line is traced across earlier file states at
the same RelativePath. It is based on the annotation algorithm's line alignment, not on searching for matching text
anywhere in the file.
_Avoid_: Text search, repository-wide provenance, semantic equivalence

### Branch Annotation Ambiguities

**Origin**:
Do not use `Origin` as a Grace branch annotation mode. Grace is centralized, and the annotation design should explain
visible content through Branches, References, and `BasedOn` relationships rather than importing Git/distributed-source
terminology.

### Branch Annotation Dialogue

Developer: "Should annotate show the origin of this line?"

Domain expert: "Say line source instead. In Grace, the answer should be a Reference in the effective branch history,
not a Git-style origin."

Developer: "Should it show who made the change?"

Domain expert: "Yes, when available. Use the Reference creator captured from event metadata, and make absence or
redaction explicit."

Developer: "If the same line text appeared earlier elsewhere in the file, is that the Introduced Reference?"

Domain expert: "No. Introduced Reference follows annotation line continuity at the exact RelativePath. It is not text
search, semantic equivalence, rename following, or repository-wide provenance."
