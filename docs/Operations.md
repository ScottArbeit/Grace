# Grace Operations

Grace Operations should first help repository owners understand what usage was observed, when it was observed, and what it covers. Charges come later, from measurements whose meaning and coverage are established.

This is the current design direction for [Issue #554](https://github.com/ScottArbeit/Grace/issues/554), following its accepted September 7 decisions D1-D5. It supersedes the old execution plan. Historical branches and incomplete experiments remain evidence to inspect selectively, not a required merge chain.

## Accepted decisions and current implementation

| Decision | Consequence |
| --- | --- |
| Usage visibility before automated billing | Establish a real source before extending pricing or close workflows. |
| Selected slices start from current main | Preserve old Operations branches and Issue #957's dirty experiment. Port only behavior the selected slice needs. |
| Start with one explicit observation | No scheduler, historical backfill, or inferred interval coverage. |
| Preserve the separate Operations worker and SQL boundary | Keep the sibling projects and Grace `OwnerId`, organization, and repository identities. Add no second content owner or accounting path. |
| Defer archive, provider costs, automatic close/corrections, and destructive reset | These do not block the first useful diagnostic. |

At source revision `9f54fe14626cd718af88890b731f8518f9b06e34`, main has the supplied-fact tracer, publisher, broker worker, SQL deduplication and aggregation. It has no complete repository-storage measurement producer. The historical pricing, archive, and billing work is not evidence that those capabilities ship on main.

The current [usage contract](../src/Grace.Types/Usage.Types.fs) has only `RepositoryStorageBytesMinute`, normalizes timestamps to a minute, and rejects quantities less than or equal to zero. The [Operations data store](../src/Grace.Operations.Data/OperationsData.fs) deduplicates fact identities and adds accepted distinct quantities. Neither behavior establishes missing source coverage or turns repeated current measurements into elapsed storage usage. Leave these contracts unchanged for the diagnostic below.

The owner observation read introduced by [Issue #1068](https://github.com/ScottArbeit/Grace/issues/1068) implements [the selected read contract](design/Operations.OwnerObservationRead.md): read a retained DirectoryVersion observation by known ID and recorded scope, using current OwnerAdmin permission and exact quantity/time strings. The [codec and permission experiments](design/Operations.OwnerObservationRead-Experiment.json) remain design evidence; route and client tests cover the implementation.

## Committed Library content declarations

### Unfinished upload cleanup

[Issue #1083](https://github.com/ScottArbeit/Grace/issues/1083) reclaims unfinished upload payload through the existing
UploadSession and ContentBlockMetadata owners. One hour after new upload progress, the retry window closes. Healthy
cleanup targets the next 15 minutes. Process recovery or a reminder more than 120 seconds overdue preserves a fresh
hour from its first observed recovery; this conservative rule can retain data longer during overload or process movement.
It does not measure outages that occur entirely between observations.

Library preparations share this clock, including a full hour after first manifest completion. Only blocks with no
completed-manifest metadata and no upload holds enter whole-block cleanup. Metadata-present and partially used blocks
remain subject to the existing 24-hour, size, churn and version rules; this change does not add a compaction worker.
Staging and permanent payload writes are fenced by persisted exact ETags. Cleanup retains its inventory after failures.
Inaccessible unfinished uploads are non-chargeable; this slice creates no quantity, minute fact, usage producer or bill.

The [portable preflight](design/UploadCleanup.Preflight.md) distinguishes actual storage effects from modeled actor
state and clocks. The broader usage measurement and persistence decisions remain separate.

### Diagnostic scope

[Issue #1077](https://github.com/ScottArbeit/Grace/issues/1077) adds the internal SystemAdmin `POST /admin/library-content-size/diagnose` diagnostic. It reads permanent accepted Library changes through the existing control record's captured `Epoch` and `CommittedCursor`. Superseded content and tombstoned items remain included through their accepted history, including changes below the public synchronization `ReplayFloor`. Pending work and records above the selected cursor contribute nothing.

The result contains explicit `Scope`, `DeclaredLogicalBytes`, `DistinctManifestCount`, `Epoch`, `CommittedCursor`, `EnumerationStartedAt` and `EnumerationFinishedAt` in the ordinary Grace success envelope. Quantities and cursor are exact nonnegative Int64 JSON numbers. The PowerShell command validates and saves the original JSON without reserializing its integers or nanosecond timestamps. SystemAdmin permission and the live, nondeleted repository scope are checked before and after the scan. Epoch change or cursor regression discards the result; an advancing tail does not change the selected prefix.

The identity is `(StoragePoolId, ManifestAddress)` within the requested repository. Each complete, agreeing immutable declaration contributes once. Reference counts do not multiply bytes. A validated existing control at cursor zero can return explicit zero only when all required source containers exist. Missing control, history, mapping or provider; an exact cursor gap; a conflicting declaration; cancellation; or Int64 overflow produces an error without a successful quantity. Retry starts a new read. The diagnostic never repairs pending work, provisions storage or writes observations, manifests, counters, SQL, broker messages or Library records.

PowerShell:

```powershell
$env:GRACE_SERVER_URI = 'https://your-grace-server'
$env:GRACE_TOKEN = '<SystemAdmin token>'
pwsh ./scripts/diagnose-library-content-size.ps1 -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath ./library-content.json
```

bash / zsh:

```bash
export GRACE_SERVER_URI='https://your-grace-server'
export GRACE_TOKEN='<SystemAdmin token>'
pwsh ./scripts/diagnose-library-content-size.ps1 -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath ./library-content.json
```

The eventual repository measurement should cover retained logical user-content bytes with complete membership for every selected content class. This diagnostic covers only committed Library declarations. Original payload lengths, orphan or pending uploads, confirmed deletion and common observation coverage remain unresolved. Issue #829 remains an unmet TextContent inclusion requirement. Do not treat `ContentVersionId` as a cross-class identity or add Library and DirectoryVersion totals: their manifests can overlap. The scan timestamps describe the read window, not a snapshot, elapsed usage interval, payload-presence guarantee, billable bytes or complete repository total. Lifetime scans can be expensive; no scheduling or capacity guarantee is made.

The [bounded provider evidence](design/Operations.LibraryContentSize-Preflight.md) separates real provider IO, seeded control/history and production-adapter checks from hosted actor acceptance. Owner capture, owner reads/listing and SQL Library observations remain deferred.

## Owner reading of retained declarations

An operator supplies the known observation ID and its recorded OwnerId, OrganizationId and RepositoryId. An OwnerAdmin on that recorded owner can read it through `GET /owner/usage/directory-version-observations/{observationId}` with the three scope IDs as query parameters. Existing SystemAdmin and SystemOperator inheritance also applies. OwnerReader or RepositoryAdmin alone does not grant access.

All four IDs must be explicit nonempty GUIDs. Names and current-configuration fallback are unsupported. The recorded scope remains valid after repository deletion or reassignment; a new repository owner does not acquire the old owner's observation. Permission is evaluated before SQL and again after the read. Revocation detected at the second check denies disclosure; the interval between that check and delivery is accepted.

The Grace success envelope contains the existing observation. `DeclaredLogicalBytes` and `DistinctContentCount` are decimal strings from `"0"` through `"9223372036854775807"`; both enumeration timestamps retain their original UTC fractional digits, including nanoseconds. Zero is a completed reading. These are retained metadata declarations, not complete repository storage, physical coverage, an elapsed interval or a charge. Do not sum different observation sources as disjoint totals. Listing and latest-reading discovery remain unavailable.

PowerShell:

```powershell
grace owner get-directory-version-observation --observation-id $observationId --owner-id $ownerId --organization-id $organizationId --repository-id $repositoryId --output Json
grace owner get-directory-version-observation --observation-id $observationId --owner-id $ownerId --organization-id $organizationId --repository-id $repositoryId --select DeclaredLogicalBytes
```

bash / zsh:

```bash
grace owner get-directory-version-observation --observation-id "$observationId" --owner-id "$ownerId" --organization-id "$organizationId" --repository-id "$repositoryId" --output Json
grace owner get-directory-version-observation --observation-id "$observationId" --owner-id "$ownerId" --organization-id "$organizationId" --repository-id "$repositoryId" --select DeclaredLogicalBytes
```

The F# SDK exposes `Owner.GetDirectoryVersionObservation(observationId, GetRepositoryParameters(...))` and reconstructs the original int64/Instant values. The CLI keeps strings in JSON, selection and examples, and prints complete values in human output. The supported [TypeScript Node facade](../sdk/typescript/grace/README.md) uses its existing generic GET request; preserve strings instead of converting to JavaScript `number` or `Date`.

Unauthenticated requests return 401 before parsing. Invalid selectors return 400; permission denial or evaluation failure returns 403; missing or conflicting scope returns 404 without stored values. A failed or cancelled read returns 503 while the connection can respond. Retrying performs a fresh read and permission checks with no write to resolve. Existing internal capture/read routes and their numeric JSON remain unchanged.

## Measurement boundary selected by Issue #1045

The [experiment record](design/Operations.Measurement-Experiment.md) selects **logical bytes declared by surviving DirectoryVersion records during an observation** as the smallest useful next diagnostic. Its successful result means the enumeration finished and its observed declarations were valid. It does not establish an atomic snapshot, physical blob presence, all retained repository content, or a billable quantity.

For each observed DirectoryVersion, fold its retained events and inspect its direct `Files`. Deduplicate whole files by repository plus the existing whole-file storage object key. Deduplicate manifests by storage pool plus manifest address and count the complete manifest logical length once. Do not multiply relationship counts, deduplicate whole files by hash alone, sum content blocks across manifests, or add `RecursiveSize`.

Logical deletion does not remove a surviving declaration. Physical deletion removes the event document from the enumerated source. The diagnostic follows that source boundary, even when a blob workflow has not finished. A result of zero is available only after the selected enumeration is exhausted and all observed declarations pass validation. Read failure, cancellation, missing required fields, conflicting sizes, scope mismatch, and overflow return an error with no quantity.

Grace's current serializer omits `FileVersion.Size` when it is zero. The diagnostic honors that encoding; explicit null, malformed, fractional, or out-of-range sizes fail. Required Created, scope, `Files`, and content-reference fields are checked before typed deserialization so their absence cannot become an apparently empty declaration. This follows the current producer contract and does not add a compatibility path.

Two matching enumerations can report observational agreement; they cannot establish that the contents existed together at one instant. The first production diagnostic needs only one complete enumeration and an explicit start/end time. It makes no agreement or snapshot promise.

## Artifact declaration diagnostic

[Issue #1062](https://github.com/ScottArbeit/Grace/issues/1062) adds `POST /admin/artifact-size/diagnose` for an authenticated SystemAdmin. It measures retained Artifact **declared bytes**, using the configured actor state storage provider. It creates no durable observation ID, usage fact or SQL row. Each invocation starts a fresh read.

The result is a standard Grace success envelope containing one server-local `ArtifactSizeDiagnostic`: `Scope`, `DeclaredArtifactBytes`, `DistinctArtifactCount`, `EnumerationStartedAt` and `EnumerationFinishedAt`. The two quantities are nonnegative `int64` values. Every result is tied to explicit owner, organization and repository GUIDs; names are rejected. The server checks that the repository exists, is not deleted and matches those IDs before and after enumeration.

The reader projects each nonempty Artifact stream from its leading Created snapshot through the current full-snapshot conversion. It counts each projected Artifact ID once, including unattached metadata, logically deleted metadata and cleanup progress until state clearance. Distinct IDs declaring identical content count separately. A zero-size Artifact contributes zero bytes and one Artifact; an empty State shell contributes neither. Current Artifact serialization explicitly includes `Size: 0` and represents an absent optional WorkItemId with an empty GUID. Missing declarations cannot use serializer defaults to become zero.

All snapshots in a stream must retain one Artifact ID, the requested three-ID scope, blob path and creation time. Duplicate projected identities count once only when their declared size and immutable source identity agree. Malformed state, conflicting identity/scope and invalid requests return an existing Grace error with HTTP 400 and no quantity. Unsupported providers, unavailable sources, failed reads, arithmetic overflow or cancellation return HTTP 503 with no quantity. A fresh attempt starts again; there is no partial result or continuation token.

`Artifact.Server.fs` owns `ArtifactSizeDiagnostic`, `validateArtifactSizeDiagnosticParameters` and `DiagnoseSize`. `Services.Actor.fs` owns decoding, projection and `readArtifactSize`, which returns bytes, count and the complete read window. Its `ActorStateStorageProvider` dispatch selects Cosmos before acquiring the existing container; MongoDB and Unknown fail before Cosmos access. No container is provisioned. There is no separate diagnostic module or compatibility wrapper.

The reader continues until exhaustion, caller cancellation or failure, with no arbitrary total-page, document, event or elapsed-time ceiling. The 256-document page-size hint controls batching only. The handler passes `RequestAborted` through the repository checks and enumeration. A large or growing source may take a long time and retains one in-memory entry per distinct Artifact. This explicit maintenance request makes no throughput promise and is not a selected routine usage producer. Its window describes a non-atomic scan of encountered records, not an instant snapshot or a complete repository storage inventory.

Artifact declarations can precede upload and remain after object deletion. This command does not read blobs or establish their presence, length, hash, orphan coverage, physical storage or provider charges. A missing object changes neither the retained declared quantity nor the command's lack of blob-presence information. DirectoryVersion and TextContent diagnostics and their durable observations remain separate sources. This route is explicitly classified as an internal operational surface; no public DTO, generated client, SDK or CLI contract is added.

PowerShell:

```powershell
$env:GRACE_SERVER_URI = 'https://your-grace-server'
$env:GRACE_TOKEN = '<SystemAdmin token>'
./scripts/diagnose-artifact-size.ps1 -OwnerId '<owner-guid>' -OrganizationId '<organization-guid>' -RepositoryId '<repository-guid>' -OutputPath './artifact-size.json'
```

bash / zsh, invoking the supported Windows PowerShell command:

```bash
export GRACE_SERVER_URI='https://your-grace-server'
export GRACE_TOKEN='<SystemAdmin token>'
pwsh -File ./scripts/diagnose-artifact-size.ps1 -OwnerId '<owner-guid>' -OrganizationId '<organization-guid>' -RepositoryId '<repository-guid>' -OutputPath './artifact-size.json'
```

The output parent directory must already exist. The command validates the full scope, source-specific quantities and ordered UTC window, preserves all nine timestamp fractional digits and the original JSON bytes, then publishes through a same-directory atomic rename. It sets connection and operation timeouts to zero; Ctrl+C cancels the request. HTTP, transport, payload and local publication failures preserve the previous output. There is no power-loss flush guarantee or coordination between concurrent local writers. A failed command exits with code 4; a successful command exits with code 0.

The [complete preflight evidence](design/Operations.ArtifactSize-Preflight.json) captures five executable sources and their hashes, dependencies, original and extracted real-Cosmos runs, and the clean before-edit checkpoint. Both runs passed 36 finite checks. They reuse [Issue #1060's current serializer and projection evidence](design/Operations.Artifact-Measurement-Experiment.md) and add stricter source validation, complete limits and error behavior. Their provider envelope and lifecycle timing are modeled. The stalled-read deadline case uses a deterministic callback, not a stalled Cosmos transport. The hosted fixture separately calls actual public Artifact Create for positive and zero declarations, checks paginated storage, and emits one exact response for Windows script replay. Seeded duplicate and lifecycle cases are explicitly labeled; they do not claim actor cleanup execution.

PR #1063's current-main refresh preserves those historical JSON records and the producer projection unchanged. Their limits describe the original experiment, not the current diagnostic. Current focused controls require successful reads beyond each former page/document/event cap, rejection of unsupported providers before container acquisition, checked overflow, cancellation and repository revalidation. The existing hosted fixture validates the relocated handler against actual public Artifact creation; the refresh adds no new persistence or cleanup behavior.

## Retained-content dispositions

| Content | Logical identity and bytes | Disposition for the selected diagnostic |
| --- | --- | --- |
| Whole files declared by surviving DirectoryVersions | Repository plus `StorageKeys.wholeFileContentObjectKey`; `FileVersion.Size` | Included as metadata declarations. Physical presence remains unobserved. |
| Manifest files declared by surviving DirectoryVersions | Storage pool plus manifest address; full `FileManifest.Size` | Included as metadata declarations. Shared blocks do not reduce this logical length. |
| Manifest contributions and repository counters | Reference relationships and their counts | Excluded as byte sources; do not add their counts to file sizes. |
| Retained TextContent | Repository plus `TextContentId`; uncompressed `Utf8ByteLength` | Excluded from this class. [Issue #1049's experiment](design/Operations.TextContent-Measurement-Experiment.md) supports a separate retained-event declaration diagnostic; current descriptions miss superseded/cleared content, and declarations do not establish blob presence or orphan coverage. |
| Attachments and artifacts | Artifact identity; metadata has declared `Size` | Excluded from this class. [Issue #1060's experiment](design/Operations.Artifact-Measurement-Experiment.md) supports a separate retained Artifact declaration measurement: project one state per Artifact ID, including unattached artifacts and cleanup residue until state clearance. Declarations precede upload and do not establish blob presence or observed object bytes. |
| Library namespace and retained content | Existing Library/content owners | No separate Library byte estimate. Existing DirectoryVersion declarations count by their identity wherever referenced; namespace entries are not multiplied into bytes. Broader Library coverage awaits its accepted implementation and evidence. |
| Orphan/missing blobs, metadata, caches, derived files, compressed representation, provider charges | Different or unavailable byte meaning | Excluded. Blob enumeration and retained metadata do not establish one shared read boundary. |

Issue #829's eventual TextContent accounting must keep description success independent of Operations availability. Identical text under different TextContent IDs represents distinct stored objects. [Issue #1049's historical captured results](design/Operations.TextContent-Measurement-Experiment.json) distinguish 2067 retained-event declared bytes from 2076 decoded observed blob bytes: one referenced object is missing and one observed object has no retained reference. Bounded orphan decompression in that experiment can report observed bytes, but the executed trailer-removal control shows that successful decoding alone does not establish the original expected content. [Issue #1051](https://github.com/ScottArbeit/Grace/issues/1051) selects a separate diagnostic of distinct TextContent bytes declared by surviving WorkItem events. It keeps handling in existing server modules and reads in `Services.Actor.fs`, with `ActorStateStorageProvider` dispatch, reading existing storage until exhaustion, caller cancellation or failure without arbitrary total-work or elapsed-time limits inherited from the experiment. The [current-main refresh](design/Operations.TextContent-Measurement-Experiment.md#current-main-refresh) records fresh replay evidence separately from historical limits and results. No routine repository-usage producer is selected here; blob reconciliation, original-content completeness and a complete repository-storage total remain deferred.

## DirectoryVersion declaration diagnostic

Implementation: [Issue #1047](https://github.com/ScottArbeit/Grace/issues/1047) adds the selected internal diagnostic after its isolated Cosmos preflight passed. The [follow-up evidence](design/Operations.Measurement-Experiment.md#issue-1047-real-provider-preflight) records the real provider check; the preceding Issue #1045 experiment remains the source of the selected byte meaning.

Quality profile: Product V1, narrowed to an authenticated SystemAdmin on the existing Grace Server/Cosmos topology and its PowerShell operator script. The query is read-only, request-local, and explicitly triggered. It promises no automatic retry, background activity, durable recovery, legacy-data compatibility, or additional provider support. Errors abort the attempt; the operator may issue a fresh request.

**Outcome:** a maintainer with `SystemAdmin` explicitly requests the DirectoryVersion declaration diagnostic and receives either a scoped observed byte count, including zero, or the existing error response with no quantity. Issue #1047 implements this selected result.

Use the existing internal `/admin` diagnostic boundary with `requireSystemAdmin`. Select one `POST /admin/directory-version-size/diagnose` query, reusing `GetRepositoryParameters` for its owner, organization, and repository identifiers. Resolve and verify their relationship through existing repository scope checks. Accept explicit identifiers only; reject name selectors rather than silently ignoring them. The source reader belongs behind Grace's server/actor storage boundary; Operations must not independently parse actor storage as a product API. Select the source using the existing `ActorStateStorageProvider` match. Cosmos uses the existing container without provisioning it; MongoDB and Unknown return an error before accessing Cosmos. Filter the repository partition and DirectoryVersion grain type, fold every returned event document, and verify owner, organization, repository, required Created data, direct-file shapes, and content identities before accumulating values.

Read until the source is exhausted or the caller cancels. There is no fixed page, DirectoryVersion, file-entry, or elapsed-time limit in the diagnostic or its operator script. The Cosmos page size remains 256 documents; pagination does not truncate the result. The request can be expensive and retains one in-memory entry per distinct content identity. Source growth can extend the scan. This explicit maintenance diagnostic makes no throughput or maximum repository capacity promise and is not the selected foundation for routine repository usage measurement. Do not expose continuation tokens, object keys, or partial counts in the successful operator result.

### Type and surface budget

One new production declaration is justified: `DirectoryVersionSizeDiagnostic`, a server-local successful response record. It keeps the observed quantity attached to its scope and read window. Reuse the existing `UsageFactScope`, `Instant`, and `GraceReturnValue`/`GraceError` response machinery. Do not add an outcome enum, nullable quantity, capture actor, journal, diagnostic ID, new request type, or storage model.

| Field | Type | Reason |
| --- | --- | --- |
| `Scope` | Existing `UsageFactScope` | Bind the result to the resolved owner, organization, and repository. Reusing this record does not create a UsageFact. |
| `DeclaredLogicalBytes` | `int64` | Nonnegative sum from the finished enumeration, including known zero. The declaration-specific name prevents a repository-total claim. |
| `DistinctContentCount` | `int64` | Explain deduplication and distinguish empty content from zero-length declarations. |
| `EnumerationStartedAt` | `Instant` | Beginning of the observation window. |
| `EnumerationFinishedAt` | `Instant` | End of the observation window; not a snapshot timestamp. |

The response type and diagnostic route name identify the fixed DirectoryVersion class. Documentation states units are logical bytes and includes the coverage exclusions. Error codes/messages use existing Grace mechanisms. A failure has no success record to accidentally interpret as zero.

| Surface | Implementation boundary |
| --- | --- |
| Source and server | Provider-dispatched reads in `Grace.Actors.Services`; handler and response record in `Grace.Server.DirectoryVersion`; no recursive-directory materialization helper. |
| Internal operator contract | One successful server-local DTO above; reuse repository parameters and standard success/error envelopes. |
| Operator entrypoint | One PowerShell script following the existing manifest-diagnosis authentication and output convention. Document scope, zero, cancellation, errors, and class-specific meaning; avoid copying its report hierarchy or signing model. |
| Public SDK, CLI, OpenAPI and generated clients | Unchanged: this is an internal SystemAdmin diagnostic like the existing manifest diagnosis route, not an owner usage API. |
| Internal route classification | Register the route in `src/OpenAPI/RouteClassification.json` as `intentionallyExcludedOperationalSurface`, alongside the existing manifest diagnostics. |
| Persistence, events, broker, SQL and Operations worker | No changes. Request-local aggregation only; a caller may save the returned diagnostic. There is no runtime capture/recovery lifecycle. |
| Tests | Actual Cosmos query/serialization integration against isolated data; duplicate identities, known zero, retained logical deletion, invalid/conflicting declaration, scope isolation, completion beyond former limits, unsupported providers, read failure/cancellation, and mixed-page observation; operator response serialization and SystemAdmin/denied reads. |

Queue this next slice with the active Libraries replacement wherever it shares `Grace.Server.fsproj` or `Startup.Server.fs`; do not write concurrently in that worktree. The diagnostic does not require changes to Library types, Common types, AppHost, package policy, or generated clients.

The isolated provider preflight established actual paginated Cosmos enumeration with Grace serialization and retained event documents. Hosted tests exercise the admin route, scope checks, and source reader against isolated repository data. Issue #1056 adds the separate durable capture/read path described below; the live diagnostic remains request-local.

### Requirement and validation map

| ID | Required behavior | Implementation | Acceptance evidence |
| --- | --- | --- | --- |
| OPS-DV-01 | Explicit SystemAdmin query with resolved, verified repository scope | Server diagnostic handler and existing admin route composition | Allowed admin response; non-admin denied; mismatched scope rejected before source read. |
| OPS-DV-02 | Sum distinct valid direct declarations from an exhausted enumeration | Server-side Cosmos read and existing DTO/key functions | Isolated actual Cosmos pages plus serializer/fold tests reproduce duplicates, known bytes, known zero, and logical retention. |
| OPS-DV-03 | Unsupported providers, invalid/conflicting data, cancellation, or failed reads yield an error without quantity | Source function and existing Grace error envelope | Each selected failure produces no successful diagnostic record; retry begins a new enumeration. |
| OPS-DV-04 | Observation window and class remain explicit; no atomic snapshot or physical-byte claim | Successful response record, operator script, and diagnostic documentation | Serialized zero/nonzero responses, mixed-page test, and rendered/documented operator output state the boundary. |
| OPS-DV-05 | No accounting publication or runtime persistence | Read-only dependency selection | Source inspection and tests establish that the diagnostic cannot call provisioning, cache materialization, broker publication, or SQL writes. |

## Run the diagnostic

Set `GRACE_SERVER_URI` and `GRACE_TOKEN` to the existing Grace Server URI and a SystemAdmin token. Supply all three explicit IDs; names are rejected. The output directory must already exist.

PowerShell:

```powershell
./scripts/diagnose-directory-version-size.ps1 `
    -OwnerId '11111111-1111-1111-1111-111111111111' `
    -OrganizationId '22222222-2222-2222-2222-222222222222' `
    -RepositoryId '33333333-3333-3333-3333-333333333333' `
    -OutputPath './directory-version-size.json'
```

bash / zsh, invoking the supported PowerShell command:

```bash
pwsh -File ./scripts/diagnose-directory-version-size.ps1 \
    -OwnerId '11111111-1111-1111-1111-111111111111' \
    -OrganizationId '22222222-2222-2222-2222-222222222222' \
    -RepositoryId '33333333-3333-3333-3333-333333333333' \
    -OutputPath './directory-version-size.json'
```

Cancel the PowerShell request with Ctrl+C to stop an unwanted scan. The server observes caller cancellation through the request token. A cancelled request publishes no diagnostic; an existing output file is preserved.

Exit `0` means a complete success envelope was validated and saved. Exit `4` means request, response validation, or local output failed. A failed request or invalid response preserves an existing destination. The command validates scope, nonnegative integer quantities, and the read window before creating a temporary file, then publishes it by a same-directory rename. It promises no power-loss flush guarantee or concurrent-writer coordination.

The saved JSON uses Grace's `ReturnValue`, `EventTime`, `CorrelationId`, and `Properties` envelope. `ReturnValue` contains the five fields above. Zero with `DistinctContentCount = 0` means no content declarations were encountered; zero with a positive count means zero-length declarations were encountered. Neither establishes physical blob presence. The server returns HTTP `400` for invalid scope or source declarations and HTTP `503` for an interrupted or failed enumeration, with `GraceError` and no successful quantity. A caller can issue a new request; partial counts and continuations are never resumed.

No usage fact, SQL row, counter change, content object, cache, reminder, or runtime diagnostic state is created. The route does not read blobs and does not verify a repository-wide snapshot. The only saved diagnostic is the caller's local output file.

## TextContent upload evidence

[Issue #1079](https://github.com/ScottArbeit/Grace/issues/1079) stores original text facts on every new compressed TextContent object. This preserves verifiable evidence when an uncertain description operation leaves an upload without a WorkItem event. The existing repository container and `text-content/{TextContentId:N}` key identify the object; equal text with distinct IDs still identifies separate objects.

| Blob metadata | Stored value |
| --- | --- |
| `grace_textcontent_format` | `1` |
| `grace_utf8_byte_length` | Original strict UTF-8 byte length as invariant decimal |
| `grace_blake3_hash` | Existing lowercase BLAKE3 hash of the original UTF-8 bytes |

The conditional upload commits metadata with the compressed body using `IfNoneMatch=*`. A conflicting write verifies the three exact values and original body against the expected TextContent reference using one download response. Missing, malformed, unsupported or conflicting evidence fails the retry without changing the object. Metadata is provider-mutable, so separate unconditioned properties and body reads must never establish retry success. Ordinary event-backed reads continue verifying their existing reference without requiring metadata.

Description success remains independent of Operations. Existing cleanup removes only newly created objects after a known rejection; uncertain outcomes retain their body and evidence. Superseding or clearing a description retains earlier objects. There is no metadata repair, backfill, new owner or deletion policy. The [captured provider experiment and reproducible source](design/Operations.TextContent-Metadata-Preflight.md) distinguish provider results from hosted acceptance and modeled failures.

This evidence is a prerequisite for later measurement. It does not establish complete membership, confirmed deletion coverage, a repository snapshot or an elapsed usage interval. Issue #829 remains unmet. Retained declarations, observed objects and overlapping content classes must not be added into a repository total or `RepositoryStorageBytesMinute`.

## TextContent declaration diagnostic

[Issue #1051](https://github.com/ScottArbeit/Grace/issues/1051) adds an explicit SystemAdmin diagnostic for distinct logical UTF-8 bytes declared by surviving WorkItem events. This helps a maintainer inspect retained text independently from DirectoryVersion declarations. A description that was superseded or cleared still contributes its earlier TextContent references while its events survive. Folding only the current description would omit those references.

The internal `POST /admin/text-content-size/diagnose` route reuses `GetRepositoryParameters`, the existing SystemAdmin authorization boundary, and the standard success/error envelopes. Supply all three non-empty GUIDs and no names. The handler verifies the repository's owner and organization before `Services.Actor.fs` reads its WorkItem partition through `ActorStateStorageProvider`. Unsupported providers fail before any Cosmos container access. The existing WorkItem server module owns the handler and response record. It does not provision storage, read blobs, call SQL, publish a UsageFact, or change a producer.

This is Product V1 on the existing Grace Server/Cosmos topology, with an explicit request and a local PowerShell output file. There is no automatic retry, background capture, durable recovery, historical interval reconstruction, or repository-total claim. A declaration remains countable when its blob is missing; an orphan blob with no surviving event reference is absent from this result. Do not add the two diagnostic quantities and label the sum complete storage usage.

### TextContent meaning and type budget

One new production type, the server-local `TextContentSizeDiagnostic` response record, contains five fields. Its source-specific quantity names prevent a saved response from being mistaken for the DirectoryVersion diagnostic. The operator scripts reject each other's response shape before publishing an output file.

| Field | Type | Meaning |
| --- | --- | --- |
| `Scope` | Existing `UsageFactScope` | Verified owner, organization and repository IDs; this creates no UsageFact. |
| `DeclaredTextContentUtf8Bytes` | `int64` | Sum of distinct valid retained declarations, in uncompressed UTF-8 bytes. |
| `DistinctTextContentCount` | `int64` | Distinct `TextContentId` values within the selected repository. |
| `EnumerationStartedAt` | Existing `Instant` | Beginning of source enumeration. |
| `EnumerationFinishedAt` | Existing `Instant` | End of enumeration; the window is not an atomic snapshot. |

An exhausted empty source, or WorkItems whose retained descriptions all have no content, produces explicit zero bytes and zero count. A present TextContent reference must have a non-empty identity, valid content hash, and positive declared length, matching the current producer's non-empty-text contract. Equal references encountered again contribute once. Equal text under distinct IDs contributes once per ID. Conflicting length or hash for the same ID aborts the attempt.

The reader requires one leading `Created` event with the requested scope in every document and examines retained `Created`, `DescriptionSet`, and `DescriptionCleared` declarations. The [serializer preflight](design/Operations.TextContentSize-Preflight.json) records the actual producer wire: absent options are explicit JSON `null`; required declaration fields cannot silently become zero or disappear during decoding. This differs from the DirectoryVersion file-size encoding and is kept in a separate reader.

Enumeration continues until exhaustion, caller cancellation or failure, with a 256-document page-size hint and no total-work or elapsed-time cap. The scan retains one entry per distinct TextContent identity, and source growth can extend its duration. Malformed or conflicting source data, cancellation, overflow, or provider failure returns an error with no successful quantity. No partial result or continuation is exposed. Retry starts a new enumeration with an empty accumulator. The earlier serializer preflight and measurement experiments remain historical evidence; their experimental limits do not constrain this diagnostic.

### Run the TextContent diagnostic

Set `GRACE_SERVER_URI` and `GRACE_TOKEN` to the existing server URI and a SystemAdmin token. The output directory must exist.

PowerShell:

```powershell
./scripts/diagnose-text-content-size.ps1 `
    -OwnerId '11111111-1111-1111-1111-111111111111' `
    -OrganizationId '22222222-2222-2222-2222-222222222222' `
    -RepositoryId '33333333-3333-3333-3333-333333333333' `
    -OutputPath './text-content-size.json'
```

bash / zsh, invoking the supported PowerShell command:

```bash
pwsh -File ./scripts/diagnose-text-content-size.ps1 \
    -OwnerId '11111111-1111-1111-1111-111111111111' \
    -OrganizationId '22222222-2222-2222-2222-222222222222' \
    -RepositoryId '33333333-3333-3333-3333-333333333333' \
    -OutputPath './text-content-size.json'
```

Cancel the PowerShell request with Ctrl+C to stop the scan. The command sets no connection or operation timeout, and the server observes the request cancellation token. Cancellation publishes no diagnostic and preserves an existing output file.

Exit `0` means the complete successful envelope was validated and saved. Exit `4` means request, response validation or local output failed. The command checks the source-specific fields, requested scope, nonnegative integer quantities and read window before writing a same-directory temporary file and renaming it to the destination. Failed requests and invalid responses preserve an existing destination. The command makes no power-loss flush or concurrent-writer guarantee.

The saved JSON uses Grace's `ReturnValue`, `EventTime`, `CorrelationId`, and `Properties` envelope. `ReturnValue` contains the five fields above. HTTP `400` reports invalid scope or source declarations; HTTP `503` reports cancellation or provider failure. Errors use `GraceError` and contain no successful diagnostic quantity.

### TextContent contract and proof map

| Requirement | Evidence |
| --- | --- |
| Admin authorization precedes parsing; scope is explicit and verified | Hosted `text diagnostic requires admin and explicit verified scope`; focused parameter test. |
| Superseded and cleared references count; identity deduplication spans pages | Unit `retained events count superseded and cleared text with identity deduplication`; hosted 258 WorkItem documents produce 16 bytes across three IDs, with no blob uploads. |
| Empty is known zero; incomplete input cannot become zero | Unit empty/null and required-field controls; hosted actual zero envelope captured after source removal. |
| Enumeration has no arbitrary work cap | Unit successful scans beyond 32 pages, 10,000 documents and 100,000 references; unsupported providers fail before container access. |
| Incomplete attempts produce no quantity | Unit cancellation before/after reads, provider failure after a valid page, conflicting identity and checked-overflow controls; hosted malformed source responses. |
| Operator output retains source meaning and preserves prior results on failure | PowerShell transport/validation/publication controls, including both directions of cross-source response rejection; actual hosted zero envelope replay. |
| No durable accounting side effect | Source depends on repository scope lookup and read-only Cosmos enumeration; no producer, SQL, broker, runtime worker, or persisted-shape changes. |

Only the internal server response, source reader, route, authorization manifest, route classification, operator script, focused tests and these docs change. Public SDK/CLI, OpenAPI schemas/generated clients, persisted events, SQL and Operations worker are unchanged because this is an internal read-only diagnostic. Route classification explicitly records `intentionallyExcludedOperationalSurface`.

The repository lookup verifies scope before reading; each retained document revalidates its Created scope before contributing. The result exposes an enumeration window, so concurrent event changes may produce a mixed observation. There is no mutation or billing publication for a stale snapshot to win. Any future durable capture needs its own acceptance design. Current DirectoryVersion and Library implementations remain unchanged by this diagnostic.

## Durable observation readiness after Issue #1054

The [actual SQL boundary experiment](design/Operations.UsageObservation-Boundary-Experiment.md) passed 21 finite controls and an extracted, hash-checked replay. Current supplied facts commit their raw row and additive minute contribution together; identical-ID retry after fresh objects adds nothing. Two different IDs carrying `17` in the same minute produce `34`. Same-ID changed quantity or scope returns `AlreadyProcessed` while preserving the original row. Zero and unsupported kinds are rejected, and the current constructor drops seconds.

That behavior does not make either source-specific diagnostic a minute accounting input. Both diagnostics need their byte/count meaning and full enumeration window, including known zero. Neither covers the complete repository, and their sum must not be labelled complete storage usage. The current positive additive `RepositoryStorageBytesMinute` contract remains unchanged.

[Issue #1056](https://github.com/ScottArbeit/Grace/issues/1056) selects and implements that first durable DirectoryVersion tracer. The accepted contract and its SQL experiment are described below. TextContent durable capture remains deferred.

## Durable DirectoryVersion declaration observations

A SystemAdmin can capture a completed declaration reading under an explicit non-empty GUID, then retrieve the identical stored observation after source changes or an uncertain response. This is a source-specific, non-billable reading. It is not repository-total storage, physical blob presence, an atomic snapshot or a minute-usage contribution.

The shared `DirectoryVersionSizeObservation` record contains `ObservationId`, the existing `UsageFactScope`, `DeclaredLogicalBytes`, `DistinctContentCount`, `EnumerationStartedAt` and `EnumerationFinishedAt`. Known zero is valid. Grace JSON writes both zero quantities explicitly and preserves Instant precision up to nine fractional digits, omitting insignificant trailing zeros. The observation is immutable; correlation ID and EventTime in each request's Grace envelope may differ.

### Capture and read contract

| Request | Behavior |
| --- | --- |
| `POST /admin/directory-version-size/observations/{observationId}` | Capture or return the prior completed observation. Body is `GetRepositoryParameters` with explicit OwnerId, OrganizationId and RepositoryId. |
| `GET /admin/directory-version-size/observations/{observationId}?OwnerId=...&OrganizationId=...&RepositoryId=...` | Read only; never enumerate source. |
| Success | HTTP 200 with `GraceReturnValue<DirectoryVersionSizeObservation>` containing the stored SQL winner. |
| Invalid identifier, missing scope, name selector or invalid source | HTTP 400 without an observation. |
| ID already bound to another owner, organization or repository | HTTP 409 without stored values. |
| Missing GET | HTTP 404. |
| Source/SQL failure or cancellation | HTTP 503 without a successful observation. An unsuccessful response does not promise that SQL did not commit. |

Both routes require current SystemAdmin authorization before parsing or storage access. A same-ID retry first checks SQL and returns a matching stored observation without accessing the repository or source. A new collection requires an existing, nondeleted repository in the complete requested scope before and immediately after the DirectoryVersion scan. Historical observations remain readable after repository deletion. No name resolution is supported.

The capture uses `DirectoryVersion.validateSizeDiagnosticParameters` and the existing `Services.Actor.fs` provider-dispatched reader with the manifest validator. It reads until exhaustion, caller cancellation or failure without a total-page, document, reference or elapsed-time ceiling. The 256-document page size controls pagination only. The HTTP request cancellation token passes through lookup, both repository checks, collection and acceptance. SQL per-command behavior remains unchanged. The enumeration window remains non-atomic; the repository recheck and SQL commit are not a distributed transaction.

### SQL acceptance and runtime

`Grace.Operations.Data.DirectoryVersionSizeObservations` exposes source-specific lookup and acceptance functions. One `ops.DirectoryVersionSizeObservation` table stores the ID, all scope IDs, nonnegative bytes/count and two ISO Instant text columns. No time-index or range-query feature is selected.

Collection holds no SQL lock. A short serializable transaction selects the ID using `UPDLOCK,HOLDLOCK`, inserts only when absent, selects the stored result, commits and then returns success. A concurrent completed reading with the same ID and scope returns the first committed row, including its original quantities and full window. A different scope gets only a conflict. Before commit, disposal rolls back an unfinished insert; after commit, a same-ID retry resolves a discarded acknowledgment through fresh SQL objects.

The existing Operations worker schema initializer creates the table, preserving its default target-database-only and explicit-create modes. AppHost forwards the existing `grace__operations__sql__connectionstring` setting to Server in DebugLocal and DebugAzure. Server startup does not require SQL; the new routes report unavailable when configuration or schema is missing. The worker, raw facts and minute aggregates remain separate and unchanged. Live diagnostics and content writes retain their existing dependencies.

### Operator command

Set `GRACE_SERVER_URI` and `GRACE_TOKEN` to the server base URI and a SystemAdmin token. Choose and retain one ObservationId explicitly. Do not replace it after an uncertain response; retry the same ID to learn whether the original attempt committed. Choose a new ID only to request another reading.

PowerShell:

```powershell
$observationId = '10560000-0000-0000-0000-000000000001'
./scripts/capture-directory-version-size.ps1 -ObservationId $observationId -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath './observation.json'
./scripts/capture-directory-version-size.ps1 -Mode Read -ObservationId $observationId -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath './observation.json'
```

bash / zsh:

```bash
observationId='10560000-0000-0000-0000-000000000001'
pwsh ./scripts/capture-directory-version-size.ps1 -ObservationId "$observationId" -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath './observation.json'
pwsh ./scripts/capture-directory-version-size.ps1 -Mode Read -ObservationId "$observationId" -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath './observation.json'
```

The script validates the returned ID, all scope IDs, nonnegative 64-bit quantities and complete UTC window before publishing the original JSON through a temporary file in the output directory. Window ordering retains all nine fractional digits. Transport, HTTP and response-validation failures leave prior output intact. The output directory must already exist. It sets connection and operation timeouts to zero; Ctrl+C cancels the request. Cancellation can follow a committed row, so retain the same ObservationId for a retry.

### Algorithm evidence and boundaries

The [captured SQL preflight](design/Operations.DirectoryVersionObservation-Preflight.json) records a **proven** result: 25 controls over real isolated SQL Server, including zero, nanosecond round trips, full scope collisions, skipped source on retry, source failure, before-write/precommit rollback, discarded postcommit acknowledgment, fresh connections, gated different windows and pre-cancellation. Seven complete rows were independently queried, captured and parsed. An extracted replay reproduced every source and result hash against baseline `6cb54792c8a7d54ba37fff83b0e192371e4898b6`.

The initial real-SQL controls passed before production edits. Artifact packaging, the complete multi-fragment SQL JSON capture, deterministic replay and captured baseline dependency hashes were completed after the first production draft. The recorded dependency hashes describe that later baseline-backed replay, not the earlier run. Failure callbacks and discarded acknowledgment simulate effect interruption; real SQL proves the transaction and durable rows. No process-kill, network-outage, power-loss, load or corruption claim is made.

PR #1057's current-main refresh updates the reader location and removes capture/operator deadlines while preserving the Data module, DTO, SQL schema and historical JSON byte-for-byte. The captured 25 SQL controls remain applicable to the unchanged lookup/check/collect/recheck/accept sequence. This is reused evidence, not a fresh SQL replay; any scan ceilings in the historical record describe that earlier execution only.

Focused tests cover Types validation/serialization, no-Aspire orchestration/error ordering, actual production Data concurrency, operator validation/publication, AppHost forwarding and hosted HTTP/Cosmos/SQL capture/retry/new-ID/read behavior. The hosted suite also checks authorization, invalid input, absent/colliding IDs, failed source, historical reads after deletion and unchanged usage tables. Local hosted execution is deferred to the isolated GitHub Validate runner because another active task shares the local Aspire container-cleanup prefixes. DebugAzure evidence is configuration-only; no Azure deployment was performed.

Public OpenAPI, generated SDK/CLI, producer events and broker contracts are unchanged: these routes are explicitly classified internal operational surfaces. This slice adds no provider abstraction, journal, pending/rejected/dispatch lifecycle, scheduler, new accounting rule, owner API, compatibility migration or broader content coverage.

## Retained TextContent observations

A SystemAdmin can capture and read an immutable retained TextContent declaration using the same SQL-first acceptance sequence as DirectoryVersion observations. The existing WorkItem reader counts distinct TextContent IDs and their declared uncompressed UTF-8 bytes across retained Created, DescriptionSet and DescriptionCleared history, including superseded references. It does not inspect blobs or measure physical storage, repository totals or an interval.

| Request | Behavior |
| --- | --- |
| `POST /admin/text-content-size/observations/{observationId}` | Capture or return the first committed TextContent row. Body is existing `GetRepositoryParameters` with explicit owner, organization and repository GUIDs. |
| `GET /admin/text-content-size/observations/{observationId}?OwnerId=...&OrganizationId=...&RepositoryId=...` | Read the stored row without repository or source access. |

Success returns `GraceReturnValue<TextContentSizeObservation>` with `ObservationId`, `Scope`, `DeclaredTextContentUtf8Bytes`, `DistinctTextContentCount`, `EnumerationStartedAt` and `EnumerationFinishedAt`. The two quantities are nonnegative 64-bit integers; known zero and all Instant fractional digits are preserved. HTTP 400 rejects invalid IDs, scope, names or malformed source; 409 rejects conflicting scope without stored values; a missing read is 404; unavailable SQL/source and cancellation return 503 without a successful observation.

Authorization precedes parsing. A matching prior SQL row bypasses the source even after repository deletion. New capture verifies current repository scope and nondeleted state before and after the retained scan, then enters the short acceptance transaction. Scope validation uses `WorkItem.validateTextContentSizeDiagnosticParameters`; `Services.Actor.fs` selects the configured provider and returns the declaration quantity, distinct count and full read window. The capture retains the verified scope and requested ObservationId. Collection runs until exhaustion, caller cancellation or failure without total-work or elapsed-time ceilings; the 256-document page size controls pagination only. The request token passes through all effects, and SQL per-command behavior remains unchanged. The window is non-atomic. A same-ID retry returns the original quantities and window; choose a new ID to recollect.

The worker initializer creates `ops.TextContentSizeObservation` through the existing Operations SQL setting. No Server startup dependency or AppHost change is added. Its row is separate from DirectoryVersion observations, raw usage facts and minute aggregates. Identical GUIDs on the two source routes address separate observations, with no combined identity or total.

### TextContent operator command

Set `GRACE_SERVER_URI` and `GRACE_TOKEN` as above. Supply an explicit ObservationId and an output file in an existing directory.

PowerShell:

```powershell
$observationId = '10580000-0000-0000-0000-000000000001'
./scripts/capture-text-content-size.ps1 -ObservationId $observationId -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath './text-observation.json'
./scripts/capture-text-content-size.ps1 -Mode Read -ObservationId $observationId -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath './text-observation.json'
```

bash / zsh:

```bash
observationId='10580000-0000-0000-0000-000000000001'
pwsh ./scripts/capture-text-content-size.ps1 -ObservationId "$observationId" -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath './text-observation.json'
pwsh ./scripts/capture-text-content-size.ps1 -Mode Read -ObservationId "$observationId" -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath './text-observation.json'
```

The script rejects a DirectoryVersion response, validates the source-specific quantities, ID, complete scope and precise UTC window, then publishes the original JSON atomically. Connection and operation timeouts are zero; Ctrl+C cancels the request. HTTP, transport and validation failures preserve prior output. Capture may have committed despite a failed response: retry the same ID. Read failures direct another read of that ID. There are no automatic retries.

The [algorithm applicability record](design/Operations.TextContentObservation-Applicability.json) maps this table and its fields to the predecessor's captured SQL experiment. Locking, absent-or-complete state, scope binding, time representation, commit ordering and retry behavior are unchanged. It reuses the recorded 25 controls and extracted replay without claiming a new experiment. Actual TextContent Data checks use isolated SQL; hosted acceptance runs in isolated GitHub Validate, followed by Windows validation of the actual hosted envelope. No new DebugAzure deployment or unsupported crash guarantee is claimed.

PR #1059's refresh consumes the reviewed PR #1052 readers and also updates its inherited DirectoryVersion observation caller from PR #1057. Both Data modules, observation DTOs, SQL schemas and historical JSON records remain unchanged. Historical scan ceilings in the applicability record describe that earlier run; the current capture behavior above supersedes them. The recorded 19 TextContent Data controls and 25 SQL preflight controls remain applicable to the unchanged acceptance sequence; they are reused evidence, not a fresh SQL replay.

## Retained Artifact observations

A SystemAdmin can capture and read an immutable retained Artifact declaration using the same SQL-first acceptance sequence as DirectoryVersion observations. The unchanged Artifact reader projects one retained declaration per ID and counts its declared bytes, including unattached, zero-byte and logically deleted state until clearance. It does not inspect blobs or measure physical storage, repository totals or an interval.

| Request | Behavior |
| --- | --- |
| `POST /admin/artifact-size/observations/{observationId}` | Capture or return the first committed Artifact row. Body is existing `GetRepositoryParameters` with explicit owner, organization and repository GUIDs. |
| `GET /admin/artifact-size/observations/{observationId}?OwnerId=...&OrganizationId=...&RepositoryId=...` | Read the stored row without repository or source access. |

Success returns `GraceReturnValue<ArtifactSizeObservation>` with `ObservationId`, `Scope`, `DeclaredArtifactBytes`, `DistinctArtifactCount`, `EnumerationStartedAt` and `EnumerationFinishedAt`. The two quantities are nonnegative 64-bit integers; known zero and all Instant fractional digits are preserved. HTTP 400 rejects invalid IDs, scope, names or malformed source; 409 rejects conflicting scope without stored values; a missing read is 404; unavailable SQL/source and cancellation return 503 without a successful observation.

Authorization precedes parsing. A matching prior SQL row bypasses the source even after repository deletion. New capture verifies current repository scope and nondeleted state before and after the retained scan, then enters the short acceptance transaction. The scan dispatches through ActorStateStorageProvider before Cosmos acquisition and continues until exhaustion, caller cancellation or source failure. Its non-atomic window and 256-item page hint remain; no total-work or elapsed-time ceiling applies. A same-ID retry returns the original quantities and window; choose a new ID to recollect.

The worker initializer creates `ops.ArtifactSizeObservation` through the existing Operations SQL setting. No Server startup dependency or AppHost change is added. Its row is separate from DirectoryVersion and TextContent observations, raw usage facts and minute aggregates. Identical GUIDs on the three source routes address separate observations, with no combined identity or total.

### Artifact operator command

Set `GRACE_SERVER_URI` and `GRACE_TOKEN` as above. Supply an explicit ObservationId and an output file in an existing directory.

PowerShell:

```powershell
$observationId = '10640000-0000-0000-0000-000000000001'
./scripts/capture-artifact-size.ps1 -ObservationId $observationId -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath './artifact-observation.json'
./scripts/capture-artifact-size.ps1 -Mode Read -ObservationId $observationId -OwnerId $ownerId -OrganizationId $organizationId -RepositoryId $repositoryId -OutputPath './artifact-observation.json'
```

bash / zsh:

```bash
observationId='10640000-0000-0000-0000-000000000001'
pwsh ./scripts/capture-artifact-size.ps1 -ObservationId "$observationId" -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath './artifact-observation.json'
pwsh ./scripts/capture-artifact-size.ps1 -Mode Read -ObservationId "$observationId" -OwnerId "$ownerId" -OrganizationId "$organizationId" -RepositoryId "$repositoryId" -OutputPath './artifact-observation.json'
```

The script rejects DirectoryVersion and TextContent responses, validates the source-specific quantities, ID, complete scope and precise UTC window, then publishes the original JSON atomically. HTTP, transport, validation and local publication failures preserve prior output. The Windows tests include an actual locked destination. The Artifact command sets connection and operation timeout ceilings to zero so the caller controls cancellation. Capture may have committed despite a failed response: retry the same ID. Read failures direct another read of that ID. There are no automatic retries.

The [algorithm applicability record](design/Operations.ArtifactObservation-Applicability.json) maps this table and its fields to the predecessor's captured SQL experiment. Locking, absent-or-complete state, scope binding, time representation, commit ordering and retry behavior are unchanged. It reuses the recorded 25 controls and extracted replay without claiming a new experiment. Actual Artifact Data checks use isolated SQL; hosted acceptance runs in isolated GitHub Validate, followed by Windows validation of the actual hosted envelope. No new DebugAzure deployment or unsupported crash guarantee is claimed.

The Artifact observation refresh preserves its Data module, DTO, schema and historical JSON. Historical scan ceilings in its applicability record describe that earlier run; current capture uses the caller-cancellable reader above. The unchanged SQL acceptance sequence reuses the recorded SQL evidence without claiming a new replay.

## Later decisions and preservation

Complete repository measurement needs an explicit coverage contract for all selected retained-content classes, unavailable observations, deletion timing, and a consistent interpretation of time. Current scans cannot reconstruct historical storage intervals. Scheduling does not solve that gap. No partial diagnostic may enter `RepositoryStorageBytesMinute`.

Keep Issue #957 paused: its eight no-hint SQL controls failed as expected, but the restored-hint passing run was not completed. Preserve its four dirty files and historical branch state. Existing historical ingestion, pricing arithmetic, journal recovery, and close tests may be reused only after the selected slice needs them and current-source validation passes.

Stop a selected slice if it requires a new content owner, billable rule, durable lifecycle, unsupported repository-total claim, shared Library write conflict, or third production enabler before a useful tracer. Keep the current experiment result and reduce scope where the accepted outcome permits it.
