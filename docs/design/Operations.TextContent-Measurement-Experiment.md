# TextContent measurement experiment

## Result and next decision

[Issue #1049](https://github.com/ScottArbeit/Grace/issues/1049), under [Issue #554](https://github.com/ScottArbeit/Grace/issues/554), asks which current Grace sources can explain retained TextContent logical UTF-8 bytes. The result is **simplified**: retained WorkItem events support a bounded declaration diagnostic, but neither current WorkItem state nor blob decompression establishes complete retained original content.

The isolated experiment passed 41 assertions using the current compiled Grace helpers, real Cosmos queries and real Azure Blob SDK calls against Azurite. Its [executable evidence](Operations.TextContent-Measurement-Experiment.json) preserves source hashes, disposable scripts, exact fixture inputs, outputs, limits and provider versions. No production implementation is added.

The smallest useful next decision is whether to implement **distinct TextContent logical bytes declared by surviving WorkItem events**, separately from the DirectoryVersion diagnostic. Its result would describe declarations observed during one bounded read window, including superseded and cleared descriptions. It would not confirm blob presence or cover objects without retained references. Blob reconciliation and original-content completeness remain deferred; this experiment does not select a production scanner, new content owner, usage schema or scheduler.

A narrower **observed decoded blob bytes** diagnostic is also feasible under the current immutable-upload producer assumptions. It would include observed orphans and require listing, downloading and decoding every selected object, with more I/O and failure points than declaration reads. It could describe the bytes returned by those bounded reads, while leaving cross-store membership, original integrity without references and atomic completeness unestablished. The manually truncated control does not establish a normal-producer defect or require new metadata or writer hardening. The declaration diagnostic is recommended first because its meaning and source are already explicit and it avoids that extra read path.

## Frozen scope

The experiment uses base `d16a9693035d375a6d9eac39419b1abbab1ead49`, the reviewed PR #1048 candidate, with eventual main target `9f54fe14626cd718af88890b731f8518f9b06e34`. PR #1046 and PR #1048 remain unmerged during this run. Baseline Admissibility is ADMISSIBLE under the Issue #1049 charter; their production changes are ancestor delivery changes, separate from this issue's three documentation and evidence paths.

Supported world: Windows, PowerShell 7.6.5, .NET SDK 10.0.400, pinned Grace source and provider packages, and disposable Cosmos/Azurite fixtures. The type budget is zero production types. Only `Limits` and `Observation` are named disposable harness records. No routes, schemas, actors, counters, indexes, project/package changes, configuration, SQL, accounting, Library work, historical intervals or billing claims are introduced.

The invariant is that an incomplete source, missing object or failed read never becomes a complete retained-content quantity or known zero. An observed source sum is always labeled by its source and limitations. Setup writes occur before observation; observer functions only query, list and download through existing clients. They never call a provisioning helper.

## Executed and modeled boundaries

| Boundary | What ran |
| --- | --- |
| Identity, strict UTF-8, compression and verification | Compiled `TextContentStorage.createIds`, `createDescription`, `compressText`, `verifyCompressedText`, scalar count and producer validation. No extracted or rewritten producer helper. |
| Event contracts and state | Actual `WorkItemEvent` Created/DescriptionSet/DescriptionCleared cases, Grace JSON options, Cosmos persisted event envelopes, deserialization and `WorkItemState.UpdateState`. |
| Immutable object effects | Real conditional upload with `IfNoneMatch = *`, actual conflict response, download and current-helper verification. This reproduces the upload effect; it does not execute `TextContentStorage.write` or its container/actor setup. |
| Known rejection and uncertain outcome | Actor outcomes are modeled. A known rejection deletes the newly created fixture blob through the SDK; an uncertain outcome leaves the uploaded object without an event. No WorkItem actor, HTTP request, crash or server recovery path ran. |
| Missing referenced object | A fixture object is uploaded, then explicitly deleted before reading. Actual download returns 404. This is a discrepancy control, not a claim that an ordinary description command deletes retained content. |
| Duplicate reference | One observed reference is duplicated in a separate enumeration control, then deduplicated by ID. No duplicate actor event or unsupported production retry history is asserted. |
| Orphan decoding | A disposable bounded `GZipStream`/strict UTF-8 reader measures observed bytes. It receives no expected reference, hash or length. Fixture-generation facts remain in the input record but are not passed as verification evidence. |

The producer's existing `read`, `write` and `deleteIfNewlyCreated` helpers use `getContainerClient`, which can create storage and fetch owner/organization actors. They are unsuitable as this experiment's read-only observer boundary. The experiment uses repository-named `BlobContainerClient` instances and existing Cosmos container clients directly.

## Fixture reconciliation

The repository is `11111111-1111-1111-1111-111111110049`. Identity below is the TextContent ID within that repository; the JSON contains full descriptions, hashes, source event payloads, exact text and compressed fixture bytes.

| Fixture | TextContent ID | Logical bytes | Compressed bytes present | Current reference | Retained event reference |
| --- | --- | ---: | ---: | --- | --- |
| A: created multibyte text | `3fe8112f-87f7-4ee2-3fe2-af3d552a0242` | 6 | 33 | No, superseded then cleared | Yes |
| B: superseding repeated ASCII | `c8ff2241-21da-5811-c724-7913475a8b8a` | 1024 | 35 | No, cleared | Yes |
| C: same text as A, different operation identity | `3deef9a0-3a54-bcd2-5ea6-052a71b56000` | 6 | 33 | Yes | Yes |
| D: varied ASCII | `5756967b-f5e7-f273-31ac-f203537ccf4a` | 1024 | 878 | Yes | Yes |
| M: missing referenced object | `8f9bf003-81ff-4b4d-64b3-e242cfff5b94` | 7 declared | Absent | Yes | Yes |
| O: modeled uncertain-outcome orphan | `838faefa-931c-df51-b8bf-7052256e434d` | 16 observed | 43 | No | No |
| K: modeled known rejection | `d3d299b0-e035-a2b0-d3c4-9397dca50e9c` | 8 in setup only | Absent after cleanup | No | No |

A and C both contain `é🙂`: two Unicode scalars, six UTF-8 bytes and different object IDs. Exact replay identity produces the same ID and the second upload conflicts; reading that existing object verifies the original text. Repository, WorkItem, correlation ID and purpose each affect identity. B and D have equal logical length but compress to 35 and 878 bytes. Upload metadata is empty, so no uncompressed-length field can be read there.

The five in-scope WorkItem event documents include one item with no description. WorkItem 1 progresses through Created(A), DescriptionSet(B), and DescriptionCleared with `TextContent = None`. Folding all three clears the current content reference without erasing A or B from retained events or storage.

| Independently observed source | Quantity | Coverage and limit |
| --- | ---: | --- |
| Current state folded from five event documents | 1037 declared bytes, 3 IDs | C + D + M. Misses A, B and O; M is absent from storage. |
| Retained event references | 2067 declared bytes, 5 IDs | A + B + C + D + M. Duplicate references count once. Does not include O or confirm M. |
| Direct reads of all retained references | No quantity | The actual 404 for M aborts the attempt. |
| Actual TextContent blob namespace | 5 IDs; 1022 compressed bytes downloaded | A + B + C + D + O. Listing alone has no logical-byte quantity. |
| Decoded observed objects | 2076 observed bytes | 2060 bytes match four retained references; 16 orphan bytes only decode successfully. This is not a complete verified retained-content quantity. |

The identities explain the difference exactly: `2067 - 7 + 16 = 2076`. Equality between any source sums would still not establish the same membership or an atomic cross-store snapshot.

## Orphan limit

The intact orphan decompresses to 16 strict UTF-8 bytes under the configured bound. Removing exactly its eight-byte GZip trailer also produced 16 bytes without an error in this executed .NET control. The JSON preserves the exact truncated base64 input and observed acceptance. This one result does not characterize other truncations, decoder versions or all invalid payloads.

Successful decompression alone therefore cannot establish the original expected content of an object whose retained reference is unavailable. The observer must not fabricate a reference from its decoded output and treat a comparison to that same output as independent validation. A separate invalid four-byte payload was stored in the actual blob prefix; its decoding error caused the whole measurement attempt to return no quantity. No producer or decoder behavior was changed.

## Scope, limits and failure results

The event query selects the actual WorkItem grain name and repository partition, then checks the repository in the folded Created state. A different grain in the same partition and a WorkItem in a foreign partition are excluded. The blob observer uses the exact `text-content/` prefix in the repository container; a `text-content-extra/` object and a foreign repository container are excluded. Both providers returned three actual pages with a requested page size of two.

| Before-read limit | Value |
| --- | ---: |
| Pages per attempt | 32 |
| Event documents or blob objects per attempt | 32 |
| References processed per attempt | 64 |
| Event response bytes or compressed download bytes per attempt | 1048576 |
| Logical bytes per attempt | 1048576 |
| Decoded bytes per unreferenced object | 262144 |
| Attempt deadline | 30000 ms |
| Provider page size request | 2 |

The byte counter covers event response streams or blob downloads, not SDK listing envelopes or HTTP overhead. Stream reads use a 4096-byte buffer; a crossing read is rejected without returning an accumulated quantity. Referenced objects use the current helper's maximum of 65536 Unicode scalars and its expected-length/hash checks. No cloud throughput, hostile payload, load or general network-bandwidth guarantee is made.

All six lowered event limits and all six lowered blob limits (page, object, reference, read-byte, logical-byte and immediate cancellation) returned incomplete with null bytes. The deadlines are passed through provider operations and orphan decoding. The reference-driven read returned no quantity on the missing blob; invalid payload and nonexistent provider containers also returned no quantity. The nonexistent blob container remained nonexistent after observation.

An existing empty repository-named blob container and an existing empty event partition each returned zero after successful exhaustion. The event partition is a fixture source boundary; repository-actor existence checks were not executed, so this is not a production repository-existence promise. After physically deleting the five WorkItem event documents, a fresh event scan returned zero declarations while the five blob objects still decoded to 2076 observed bytes, all without retained references. Zero declarations thus does not mean no retained content.

Every attempt starts with fresh local accumulators and new provider reads. A fresh successful attempt after the failure controls returned the same observed membership and quantity. Setup, mutation controls and observation windows are separate. No result is an atomic snapshot, historical interval, original-content completeness statement or billable total.

## Reproduction and validation

Use the pinned checkout and the two cached image IDs recorded in the JSON. Docker Desktop must be running. The startup script checks that only its chosen names are new and that loopback ports 18081 and 11000 are free. It uses no cloud account or user content. If its exact names already exist, inspect and retire those owned fixtures before starting a new run; do not invoke shared Aspire cleanup.

PowerShell:

```powershell
$repositoryRoot = (Get-Location).Path
$evidence = Get-Content ./docs/design/Operations.TextContent-Measurement-Experiment.json -Raw | ConvertFrom-Json
$experimentRoot = Join-Path ([IO.Path]::GetTempPath()) ('grace-1049-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
foreach ($file in $evidence.harnessFiles) {
    $path = Join-Path $experimentRoot $file.name
    [IO.File]::WriteAllText($path, $file.content)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Extracted harness hash mismatch: $($file.name)"
    }
}
& "$experimentRoot/Start-Fixtures.ps1"
try {
    & "$experimentRoot/Run-Experiment.ps1" -RepositoryRoot $repositoryRoot
} finally {
    & "$experimentRoot/Stop-Fixtures.ps1"
}
```

The run script builds the smallest project exposing the required helpers, `Grace.Server`, in Release with copied dependencies. It generates the FSI load list from those outputs. `-SkipBuild` was used for corrected harness runs only after the matching successful Release build, with unchanged executable source. Normal reproduction should build. This Windows-only fixture does not supply a bash / zsh variant.

Local validation comprises the successful Release build (zero warnings/errors), 41 executed assertions, PowerShell and JSON parsing, extracted-content SHA-256 reconciliation, focused Markdown lint and `git diff --check`. Required current-head GitHub Validate and independent reviews belong to the controller's delivery gates. Local Fast/Full and broad test suites are unnecessary for this documentation-only change.

The two issue-owned containers were stopped after capture. Their logs and the disposable harness remain outside the repository. Other containers and worktrees were preserved. The committed JSON is sufficient to recreate the executable experiment after task-worktree cleanup.
