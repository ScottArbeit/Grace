# TextContent metadata preflight

## Accepted slice

[Issue #1079](https://github.com/ScottArbeit/Grace/issues/1079) preserves original UTF-8 length and BLAKE3 evidence with each compressed TextContent upload. The accepted T1-T4 and TC1-TC7 contract uses the existing TextContent reference and object key, with no new named production type, store, owner or lifecycle. The base is `e42b7c50e007a7ed3dbe6be45a5ce7938398e979`.

The upload condition remains `IfNoneMatch=*`. Metadata and bytes commit together. A conflict can return write success only after the expected metadata and body verify from one response. Ordinary reads remain event-backed. D1-D5 and C1-C5 remain accepted; complete membership, deletion and interval portions of C6 remain deferred. Issue #829 remains unmet.

## Captured provider results

The [portable evidence bundle](Operations.TextContent-Metadata-Preflight.json) embeds original source, input, results, receipts and logs as exact UTF-8 file bytes encoded in Base64, each with SHA-256. Its parsed `Results` contains all 16 passing Azurite controls. This is reused September 9 evidence, not a new run of the experiment.

The experiment used .NET SDK 10.0.401, Azure.Storage.Blobs 12.29.1 and Blake3 3.0.2. It reused the captured six-byte UTF-8 input and its 33-byte compressed payload. A fresh client could recover both body and metadata after the upload response was discarded. Conflicting uploads preserved the ETag and first object; staged blocks were unreadable before block-list commit and complete with metadata after commit. Distinct IDs paginated separately. Missing or wrong evidence failed. A cancelled delete retained the object, acknowledged deletion removed it, and fresh HEAD returned 404. Deliberate metadata mutation caused a stale ETag-conditioned download to fail with 412.

The upload-response loss and restart are modeled by discarding a response and creating new clients. No process was killed and no WorkItem actor ran in this provider experiment. A trailerless GZip control still yielded the original verified logical bytes, so these results do not claim container integrity. The explicit block staging control demonstrates provider publication; it does not claim that the production writer exercised multipart transfer at a custom configured size. No cloud failover, atomic repository inventory, exact deletion timestamp or usage interval was tested.

The current base's writer, TextContent record, key and hash helper match the captured source inputs. Historical WorkItem orchestration and Services changes recorded in `SourceApplicability` do not turn provider effects into actor tests. The implementation adds metadata at the captured conditional upload boundary and uses the retry download's own metadata and content together.

## Extract and reproduce

Extract into a new disposable directory. The embedded original runner preserves earlier containers and stops only the uniquely named container it creates. It requires Windows PowerShell 7.6, Docker, the dependencies above, the Grace checkout and free loopback port 11010. Its historical image identifier `76b8127d608f` must already exist locally. The verified registry reference is `mcr.microsoft.com/azure-storage/azurite@sha256:76b8127d608fab8287a14a4bfeb9a5502cdcffb4bf1e86f09f324ebb0e70edba`; obtain that image before running on another machine. Substituting an image changes the experiment inputs and needs a new receipt. Extraction and hash verification do not start containers.

PowerShell:

```powershell
$bundle = Get-Content docs/design/Operations.TextContent-Metadata-Preflight.json -Raw | ConvertFrom-Json
$destination = Join-Path ([IO.Path]::GetTempPath()) ('grace-text-evidence-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($file in $bundle.Files) {
    $path = Join-Path $destination $file.Name
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($file.Base64))
    if ((Get-FileHash -LiteralPath $path).Hash -ne $file.SHA256) { throw "Hash mismatch: $($file.Name)" }
}
& (Join-Path $destination 'Run-Experiment.ps1') -RepositoryRoot (Get-Location).Path
```

The runner is Windows-specific because it uses `Get-NetTCPConnection` and `curl.exe`; there is no equivalent bash / zsh execution claim.

## Acceptance coverage

| Contract | Test or inspection |
| --- | --- |
| TC1: create/set original evidence | `DescriptionLifecyclePreservesOriginalBlobEvidence` checks multibyte and default-boundary text through hosted create/set and direct storage reads. |
| TC2: exact retry and first-object preservation | Lifecycle test checks unchanged ETags; existing replay corruption controls check bodies. |
| TC3: rejected metadata and ordinary reads | `RetryMetadataRejectsMissingOrConflictingEvidence` and `DescriptionReplayRejectsMetadataWithoutRepair`. |
| TC4: retained uncertain upload | `DescriptionPostUploadFailureRetainsEvidenceForRetry` resets the existing post-upload gate connection, checks failure/no append, then retries the retained object. This is an injected gate failure, not an actor crash. Existing production cleanup classification and ownership are unchanged. |
| TC5: distinct identity and retained history | Lifecycle test checks distinct create/set IDs with equal text and unchanged earlier objects after clear. |
| TC6: limits and one-revision verification | Default-boundary hosted case; `CustomCharacterBoundaryPreservesUtf8Evidence` checks a 70,000-scalar custom boundary in pure validation/compression. Existing hosted repository/authentication/replay cases remain. Retry uses one download response. |
| TC7: unchanged external contracts | Only the approved storage, tests and documentation paths change. No DTO, event, route, SDK, CLI, SQL or Operations dependency changes. |

Hosted assertions are supplied for the required current-head GitHub Validate gate. A local hosted run was not used to duplicate that gate. The custom-boundary test exercises validation and compression, not a hosted custom-configuration deployment or production multipart upload. Known-rejection cleanup and failed cleanup retain their existing orchestration; the captured cancellation/deletion controls are provider-only. These limits must remain explicit in review and delivery reporting.

Local candidate validation passed all 22 `TextContentServerTests` after a matching Release build. Both the unit and hosted test projects built with zero warnings and errors. Fantomas formatted the three touched F# files; markdownlint checked the three changed Markdown documents without findings. All ten embedded files extracted with matching hashes and the embedded PowerShell runner parsed successfully. No behavioral RED against the original hosted writer was run; the original source lacked upload metadata and used body-only retry verification. Required GitHub Validate and independent review remain the delivery gates.
