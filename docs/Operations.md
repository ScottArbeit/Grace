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

## Measurement boundary selected by Issue #1045

The [experiment record](design/Operations.Measurement-Experiment.md) selects **logical bytes declared by surviving DirectoryVersion records during an observation** as the smallest useful next diagnostic. Its successful result means the enumeration finished and its observed declarations were valid. It does not establish an atomic snapshot, physical blob presence, all retained repository content, or a billable quantity.

For each observed DirectoryVersion, fold its retained events and inspect its direct `Files`. Deduplicate whole files by repository plus the existing whole-file storage object key. Deduplicate manifests by storage pool plus manifest address and count the complete manifest logical length once. Do not multiply relationship counts, deduplicate whole files by hash alone, sum content blocks across manifests, or add `RecursiveSize`.

Logical deletion does not remove a surviving declaration. Physical deletion removes the event document from the enumerated source. The diagnostic follows that source boundary, even when a blob workflow has not finished. A result of zero is available only after the selected enumeration is exhausted and all observed declarations pass validation. Read failure, cancellation, missing required fields, conflicting sizes, scope mismatch, and overflow return an error with no quantity.

Grace's current serializer omits `FileVersion.Size` when it is zero. The diagnostic honors that encoding; explicit null, malformed, fractional, or out-of-range sizes fail. Required Created, scope, `Files`, and content-reference fields are checked before typed deserialization so their absence cannot become an apparently empty declaration. This follows the current producer contract and does not add a compatibility path.

Two matching enumerations can report observational agreement; they cannot establish that the contents existed together at one instant. The first production diagnostic needs only one complete enumeration and an explicit start/end time. It makes no agreement or snapshot promise.

## Retained-content dispositions

| Content | Logical identity and bytes | Disposition for the selected diagnostic |
| --- | --- | --- |
| Whole files declared by surviving DirectoryVersions | Repository plus `StorageKeys.wholeFileContentObjectKey`; `FileVersion.Size` | Included as metadata declarations. Physical presence remains unobserved. |
| Manifest files declared by surviving DirectoryVersions | Storage pool plus manifest address; full `FileManifest.Size` | Included as metadata declarations. Shared blocks do not reduce this logical length. |
| Manifest contributions and repository counters | Reference relationships and their counts | Excluded as byte sources; do not add their counts to file sizes. |
| Retained TextContent | Repository plus `TextContentId`; uncompressed `Utf8ByteLength` | Excluded from this class. [Issue #1049's experiment](design/Operations.TextContent-Measurement-Experiment.md) supports a separate retained-event declaration diagnostic; current descriptions miss superseded/cleared content, and declarations do not establish blob presence or orphan coverage. |
| Attachments and artifacts | Artifact identity; metadata has declared `Size` | Excluded from this class. Attachment links alone miss unlinked artifacts; metadata precedes upload and does not establish blob presence. |
| Library namespace and retained content | Existing Library/content owners | No separate Library byte estimate. Existing DirectoryVersion declarations count by their identity wherever referenced; namespace entries are not multiplied into bytes. Broader Library coverage awaits its accepted implementation and evidence. |
| Orphan/missing blobs, metadata, caches, derived files, compressed representation, provider charges | Different or unavailable byte meaning | Excluded. Blob enumeration and retained metadata do not establish one shared read boundary. |

Issue #829's eventual TextContent accounting must keep description success independent of Operations availability. Identical text under different TextContent IDs represents distinct stored objects. [Issue #1049's historical captured results](design/Operations.TextContent-Measurement-Experiment.json) distinguish 2067 retained-event declared bytes from 2076 decoded observed blob bytes: one referenced object is missing and one observed object has no retained reference. Bounded orphan decompression in that experiment can report observed bytes, but the executed trailer-removal control shows that successful decoding alone does not establish the original expected content. The next recommended decision is a separate diagnostic of distinct TextContent bytes declared by surviving WorkItem events. If selected, it should keep handling in existing server modules and reads in `Services.Actor.fs`, with `ActorStateStorageProvider` dispatch, reading existing storage until exhaustion, caller cancellation or failure without arbitrary total-work or elapsed-time limits inherited from the experiment. The [current-main refresh](design/Operations.TextContent-Measurement-Experiment.md#current-main-refresh) records fresh replay evidence separately from historical limits and results. No implementation or routine repository-usage producer is selected here; blob reconciliation, original-content completeness and a complete repository-storage total remain deferred.

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

The isolated provider preflight established actual paginated Cosmos enumeration with Grace serialization and retained event documents. Hosted tests exercise the admin route, scope checks, and source reader against isolated repository data. A new durable acceptance path remains deferred until a supported measurement outcome needs it.

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

## Later decisions and preservation

Complete repository measurement needs an explicit coverage contract for all selected retained-content classes, unavailable observations, deletion timing, and a consistent interpretation of time. Current scans cannot reconstruct historical storage intervals. Scheduling does not solve that gap. No partial diagnostic may enter `RepositoryStorageBytesMinute`.

Keep Issue #957 paused: its eight no-hint SQL controls failed as expected, but the restored-hint passing run was not completed. Preserve its four dirty files and historical branch state. Existing historical ingestion, pricing arithmetic, journal recovery, and close tests may be reused only after the selected slice needs them and current-source validation passes.

Stop a selected slice if it requires a new content owner, billable rule, durable lifecycle, unsupported repository-total claim, shared Library write conflict, or third production enabler before a useful tracer. Keep the current experiment result and reduce scope where the accepted outcome permits it.
