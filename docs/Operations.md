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

The [experiment record](design/Operations.Measurement-Experiment.md) selects **logical bytes declared by surviving DirectoryVersion records during an observation** as the smallest useful next diagnostic. Its successful result means the bounded enumeration finished and its observed declarations were valid. It does not establish an atomic snapshot, physical blob presence, all retained repository content, or a billable quantity.

For each observed DirectoryVersion, fold its retained events and inspect its direct `Files`. Deduplicate whole files by repository plus the existing whole-file storage object key. Deduplicate manifests by storage pool plus manifest address and count the complete manifest logical length once. Do not multiply relationship counts, deduplicate whole files by hash alone, sum content blocks across manifests, or add `RecursiveSize`.

Logical deletion does not remove a surviving declaration. Physical deletion removes the event document from the enumerated source. The diagnostic follows that source boundary, even when a blob workflow has not finished. A result of zero is available only after the selected enumeration is exhausted and all observed declarations pass validation. Bounds, read failure, cancellation, missing required fields, conflicting sizes, scope mismatch, and overflow return an error with no quantity.

Two matching enumerations can report observational agreement; they cannot establish that the contents existed together at one instant. The first production diagnostic needs only one bounded enumeration and an explicit start/end time. It makes no agreement or snapshot promise.

## Retained-content dispositions

| Content | Logical identity and bytes | Disposition for the selected diagnostic |
| --- | --- | --- |
| Whole files declared by surviving DirectoryVersions | Repository plus `StorageKeys.wholeFileContentObjectKey`; `FileVersion.Size` | Included as metadata declarations. Physical presence remains unobserved. |
| Manifest files declared by surviving DirectoryVersions | Storage pool plus manifest address; full `FileManifest.Size` | Included as metadata declarations. Shared blocks do not reduce this logical length. |
| Manifest contributions and repository counters | Reference relationships and their counts | Excluded as byte sources; do not add their counts to file sizes. |
| Retained TextContent | `TextContentId`; uncompressed `Utf8ByteLength` | Excluded from this class. Issue #829 requires retained superseded/cleared descriptions to count eventually; current descriptions alone miss history. |
| Attachments and artifacts | Artifact identity; metadata has declared `Size` | Excluded from this class. Attachment links alone miss unlinked artifacts; metadata precedes upload and does not establish blob presence. |
| Library namespace and retained content | Existing Library/content owners | No separate Library byte estimate. Existing DirectoryVersion declarations count by their identity wherever referenced; namespace entries are not multiplied into bytes. Broader Library coverage awaits its accepted implementation and evidence. |
| Orphan/missing blobs, metadata, caches, derived files, compressed representation, provider charges | Different or unavailable byte meaning | Excluded. Blob enumeration and retained metadata do not establish one shared read boundary. |

Issue #829's eventual TextContent accounting must keep description success independent of Operations availability. Identical text under different TextContent IDs represents distinct stored objects. Gzip upload does not itself provide the retained uncompressed length for an orphan without a reference. These gaps preclude a complete repository-storage total today.

## Selected next production slice

Readiness: **Design-ready recommendation, with a bounded provider preflight required before production implementation.** D1-D5 are accepted; the concrete route, fixed limits, and single-record budget below are the experiment's recommendation for the next issue. Its source adapter must pass the deciding preflight, and the controller must record the next issue's accepted scope before edits.

Quality profile for that slice: Product V1, narrowed to an authenticated SystemAdmin on the existing Grace Server/Cosmos topology and its PowerShell operator script. The query is read-only, request-local, and explicitly triggered. It promises no automatic retry, background activity, durable recovery, legacy-data compatibility, or additional provider support. Errors abort the attempt; the operator may issue a fresh request.

**Outcome:** a maintainer with `SystemAdmin` explicitly requests the DirectoryVersion declaration diagnostic and receives either a scoped observed byte count, including zero, or the existing error response with no quantity. This is the next recommendation from the experiment; it is not implemented by Issue #1045.

Use the existing internal `/admin` diagnostic boundary with `requireSystemAdmin`. Select one `POST /admin/directory-version-size/diagnose` query, reusing `GetRepositoryParameters` for its owner, organization, and repository identifiers. Resolve and verify their relationship through existing repository scope checks. Accept explicit identifiers only; reject name selectors rather than silently ignoring them. The source reader belongs behind Grace's server/actor storage boundary; Operations must not independently parse actor storage as a product API. Use the existing Cosmos container without provisioning it. Filter the repository partition and DirectoryVersion grain type, fold every returned event document, and verify owner, organization, repository, required Created data, direct-file shapes, and content identities before accumulating values.

Use fixed initial limits: at most 32 pages, at most 10,000 DirectoryVersion documents, at most 100,000 direct file references, and a 30-second linked cancellation deadline; set page size to 256 documents. Limits constrain work, not sampling. A remaining continuation at a limit means error, never successful truncation. Do not expose continuation tokens, object keys, or partial counts in the successful operator result. These conservative limits are proposed operating limits, not measured Cosmos performance results.

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

| Surface | Selected work for the next slice |
| --- | --- |
| Source and server | One bounded read-only source function and one SystemAdmin diagnostic query; no recursive-directory materialization helper. |
| Internal operator contract | One successful server-local DTO above; reuse repository parameters and standard success/error envelopes. |
| Operator entrypoint | One PowerShell script following the existing manifest-diagnosis authentication and output convention. Document scope, zero, limits, errors, and class-specific meaning; avoid copying its report hierarchy or signing model. |
| Public SDK, CLI, OpenAPI and generated clients | Unchanged: this is an internal SystemAdmin diagnostic like the existing manifest diagnosis route, not an owner usage API. |
| Persistence, events, broker, SQL and Operations worker | No changes. Request-local aggregation only; a caller may save the returned diagnostic. There is no runtime capture/recovery lifecycle. |
| Tests | Actual Cosmos query/serialization integration against isolated data; duplicate identities, known zero, retained logical deletion, invalid/conflicting declaration, scope isolation, limits, read failure/cancellation, and mixed-page observation; operator response serialization and SystemAdmin/denied reads. |

Queue this next slice with the active Libraries replacement wherever it shares `Grace.Server.fsproj` or `Startup.Server.fs`; do not write concurrently in that worktree. The diagnostic does not require changes to Library types, Common types, AppHost, package policy, or generated clients.

The deciding preflight is the real, isolated Cosmos enumeration through the selected storage adapter. The local experiment has not established provider behavior or route authorization. If that preflight fails, return a source-specific diagnostic blocker; do not substitute fixture results as product integration evidence. A new durable acceptance path is deferred until a supported measurement outcome needs it. No production enabling PR has been consumed by this discovery work.

### Requirement and validation map

| ID | Required behavior | Likely implementation | Acceptance evidence |
| --- | --- | --- | --- |
| OPS-DV-01 | Explicit SystemAdmin query with resolved, verified repository scope | Server diagnostic handler and existing admin route composition | Allowed admin response; non-admin denied; mismatched scope rejected before source read. |
| OPS-DV-02 | Sum distinct valid direct declarations from an exhausted bounded enumeration | Server-side Cosmos read and existing DTO/key functions | Isolated actual Cosmos pages plus serializer/fold tests reproduce duplicates, known bytes, known zero, and logical retention. |
| OPS-DV-03 | Bounds, invalid/conflicting data, cancellation, or failed reads yield an error without quantity | Source function and existing Grace error envelope | Each selected failure produces no successful diagnostic record; retry begins a new enumeration. |
| OPS-DV-04 | Observation window and class remain explicit; no atomic snapshot or physical-byte claim | Successful response record, operator script, and diagnostic documentation | Serialized zero/nonzero responses, mixed-page test, and rendered/documented operator output state the boundary. |
| OPS-DV-05 | No accounting publication or runtime persistence | Read-only dependency selection | Source inspection and tests establish that the diagnostic cannot call provisioning, cache materialization, broker publication, or SQL writes. |

## Later decisions and preservation

Complete repository measurement needs an explicit coverage contract for all selected retained-content classes, unavailable observations, deletion timing, and a consistent interpretation of time. Current scans cannot reconstruct historical storage intervals. Scheduling does not solve that gap. No partial diagnostic may enter `RepositoryStorageBytesMinute`.

Keep Issue #957 paused: its eight no-hint SQL controls failed as expected, but the restored-hint passing run was not completed. Preserve its four dirty files and historical branch state. Existing historical ingestion, pricing arithmetic, journal recovery, and close tests may be reused only after the selected slice needs them and current-source validation passes.

Stop a selected slice if it requires a new content owner, billable rule, durable lifecycle, unsupported repository-total claim, shared Library write conflict, or third production enabler before a useful tracer. Keep the current experiment result and reduce scope where the accepted outcome permits it.
