# Artifact declarations and observed object bytes

## Question and result

What can current retained Artifact state truthfully count, and how does that quantity differ from actual object bytes during upload and cleanup?

[Issue #1060](https://github.com/ScottArbeit/Grace/issues/1060), part of [Issue #554](https://github.com/ScottArbeit/Grace/issues/554), answers this question with isolated Cosmos and Azurite fixtures. The result supports **retained Artifact declared bytes**: project each retained Artifact event stream, then count each scoped Artifact identity once. It does not establish physical storage, complete repository coverage, or a time interval.

The completed fixture has six retained declarations totaling **52 declared bytes** and six existing objects totaling **59 observed payload bytes**. The identity sets differ. One zero-byte declaration has no object; an 11-byte object has no retained declaration. A deliberately mismatched declaration states nine bytes for a five-byte object. These differences remain explicit instead of being combined into a repository total.

The [complete executable evidence](Operations.Artifact-Measurement-Experiment.json) contains the sources, current producer and dependency hashes, all input JSON, actual provider captures, assertion results, exact commands, and extraction/replay evidence. No production types or behavior change.

## Supported experiment and boundaries

### September 8 applicability refresh

This PR is rebased on main `16897c7fd1fd65dbdb90cbecfe778e0eb20cdbbf`, which includes landed PR #1059 and the newer Library work. The original JSON bundle and provider captures remain unchanged. Artifact Types, the Artifact actor and the Artifact server producer are byte-identical to the experiment base. WorkItem changes add the separate TextContent diagnostic; its Artifact production paths are unchanged. The newer Library changes add an unrelated ItemKind declaration in Common.Types.fs without changing these Artifact inputs. These comparisons preserve the captured source meaning without claiming another provider run or unchanged whole-assembly hashes.

The finite bounds below belong to the disposable experiment. The current production direction follows the accepted DirectoryVersion and TextContent changes: the existing Artifact server module owns its response and HTTP operation, actor Services dispatches the read by storage provider, and enumeration runs to exhaustion, cancellation or failure without arbitrary total caps. That production diagnostic remains a separate PR.

### Original execution

The Discovery base is `439e99e0b77d9b35c676c912ed1427060c15d96b`, the reviewed PR #1059 candidate. Eventual delivery preserves the existing delta against main `9f54fe14626cd718af88890b731f8518f9b06e34`. Execution uses Windows PowerShell 7.6, .NET SDK 10.0.400, and the current compiled Grace Actors/Shared/Types output from the assigned Issue #1060 worktree. Cosmos 3.62.1 and Azure Storage Blobs 12.29.1 use isolated local emulators with pinned image digests recorded in JSON.

| Seam | Executed or modeled |
| --- | --- |
| `ArtifactMetadata`, `ArtifactCreated`, `ArtifactCommand`, `ArtifactEvent` conversion and JSON | Current compiled Grace records, conversion methods and serializer. The exact JSON is captured. |
| Event projection | Current compiled `ArtifactMetadata.UpdateDto`, folded sequentially as in `ArtifactActor.OnActivateAsync`. No rewritten reducer. |
| Normal object path | Unchanged, hashed extraction of `Artifact.Server.buildBlobPath` and `buildDeterministicBlobPath`; the time-based helper is executed. |
| Cosmos write/query | Actual provider writes, pages, response JSON, scoped projection, empty State, document deletion and removed-source errors. The grain envelope is hand-built for the fixture, not captured from a hosted actor. |
| Blob effects | Actual uploads, listing, payload downloads, existence/property reads, deletion and removed-source errors. |
| Create and cleanup sequencing | Modeled interruptions and snapshot calls following current source ordering. No HTTP handler, hosted actor, WorkItem unlink, reminder, authorization or complete cleanup lifecycle is exercised. |
| Mismatch and orphan | Deliberate controls. The experiment does not claim a normal producer has created either state. |
| State clearance | An empty `State` shell is a modeled representation; actual Cosmos document removal is also executed. No claim about the precise envelope produced by hosted `ClearStateAsync`. |

Only two named harness types exist: `Limits` holds the fixed read budget, and `Observation` keeps complete optional quantities separate from partial work, timings and rows. Both are embedded external experiment code. Production declaration delta: zero. There is no reusable measurement framework.

## Current producer and effect order

`Artifact.Server.Create` accepts `Size >= 0`. It obtains an upload URI, persists `ArtifactCommand.Create`, and only then returns that URI to the caller. An interruption before the caller uploads therefore leaves a supported metadata-only declaration. `WorkItem.Server` also persists generated Artifact metadata before uploading content; its generated size comes from the UTF-8 content length. These source facts support the modeled upload boundary without implying that the HTTP handlers were invoked.

Each `ArtifactEvent` contains a complete snapshot. `ArtifactCreated.FromMetadata`, `ArtifactEvent.FromMetadata` and `ToMetadata` retain the declaration. `UpdateDto` replaces the projection with the event snapshot. Consequently, four lifecycle snapshots of one 64-byte Artifact count as one 64-byte declaration, even when the final snapshot records blob deletion and WorkItem link removal.

`ArtifactActor.ConvergePhysicalDeletion` checks the retained deletion identity, repository and WorkItem, deletes the blob, records `BlobDeleted`, removes the WorkItem link, records `WorkItemLinkRemoved`, and clears state. The experiment executes blob deletion and Cosmos effects at those selected boundaries. It does not simulate the checks or link mutation as successful production calls.

Artifact identity appears in both normal path forms. Equal content under distinct Artifact IDs can therefore occupy two objects. `WorkItemId` is optional, so enumeration through WorkItem links cannot establish complete Artifact declaration coverage.

The actual `ArtifactCreated` wire form includes `"Size": 0` and an empty GUID for absent `WorkItemId`. Conversion restores `WorkItemId = None`. The F# record serializer preserves zero here despite the general `WhenWritingDefault` setting. A failed initial harness assumption about omission was corrected from the captured wire evidence; no production serializer was changed.

## Finite cases

| Case | Result | Observation |
| --- | --- | --- |
| A1: four lifecycle snapshots | PASS | Created, LogicalDeleted, BlobDeleted and WorkItemLinkRemoved retain one 64-byte projection; summing their sizes would incorrectly yield 256 bytes. |
| A2: metadata before upload | PASS | One 64-byte declaration exists with no blob and no observed blob length. Actual upload changes existence/length to 64 without changing the declaration. |
| A3: cleanup residue and removal | PASS | Actual blob deletion leaves the 64-byte declaration. Empty State is excluded while the source exists; actual document removal also leaves a completed zero-declaration read. |
| A4: identity and repeat reads | PASS | Two IDs with identical 13-byte payloads have distinct normal paths and occupy two objects. Repeated fresh reads reproduce the same six-identity declaration quantity. |
| A5: three zero-related states | PASS | Existing empty sources return known zero with count zero. An actual zero-byte object exists. A separate zero-byte declaration has a missing object and no blob length. |
| A6: links and scope | PASS | Unattached Artifacts are included. Foreign repository, owner, organization and grain controls are excluded. The six declarations exceed the one declaration reachable from the fixture's WorkItem links. |
| A7: finite enumeration | PASS | Real Cosmos and blob pagination exhaust before success. Lower page/document/event/blob/byte caps, pre-cancellation and actual removal of dedicated empty sources return no complete quantity. |
| A8: unmatched or different bytes | PASS | An 11-byte object has no retained declaration. A deliberate nine-byte declaration has five actual bytes. Independent path/length/content-hash reconciliation identifies every expected object. |

Each measurement begins with fresh provider reads. Bounds are fixed before the attempt: 32 pages, 32 documents, 64 events, 32 objects, 1,048,576 consumed payload/response bytes, and a 30-second cancellation deadline; provider page-size requests are two entries. The byte limit covers Cosmos response streams and downloaded blob content. SDK transport headers and its internally decoded blob-list response are not counted as payload bytes. A byte-cap attempt can consume the buffer that crosses the limit but cannot emit a complete quantity.

The readers consume existing clients and never provision storage. Fixture setup and the intentional empty-source removal occur outside measurement windows. Remaining continuation, a cap or provider error means incomplete/error with null complete bytes and count. Actual pre-cancellation is tested; deadline expiry during a stalled transport is not separately injected. Pagination and cross-store agreement do not establish an atomic snapshot. Object observations cover current objects under `grace-artifacts/`; they exclude versions, snapshots, other prefixes, provider overhead and charges.

## Independent identity ledger

Artifact IDs use `77777777-7777-7777-7777-` followed by the numeric suffix below. Exact repository, owner, organization, WorkItem, path, event and observation-window values are in the evidence. Original and replay use fresh repository IDs so both datasets can be retained; Artifact IDs, content hashes and expected quantities remain comparable.

| Suffix | Retained declaration | Declared bytes | Object exists | Observed payload bytes | Role |
| --- | --- | --- | --- | --- | --- |
| `000000000002` | Yes | 13 | Yes | 13 | Linked twin A |
| `000000000003` | Yes | 13 | Yes | 13 | Unattached twin B, equal content |
| `000000000004` | Yes | 0 | Yes | 0 | Actual zero-byte object |
| `000000000005` | Yes | 0 | No | Unknown | Metadata-only zero declaration |
| `000000000006` | Yes | 17 | Yes | 17 | Unattached content |
| `000000000007` | Yes | 9 | Yes | 5 | Deliberate mismatch control |
| `000000000008` | No | N/A | Yes | 11 | Separate orphan control |

The declaration ledger sums to `13 + 13 + 0 + 0 + 17 + 9 = 52`. The existing-object ledger sums to `13 + 13 + 0 + 17 + 5 + 11 = 59`. Missing is not assigned zero. Declared bytes on these six identities remain a valid declaration sum even when an object is missing or a deliberately different payload is present.

The 64-byte lifecycle identity `000000000001` is removed before this ledger. Foreign identities `000000000009` through `000000000012` test repository, owner, organization and grain exclusion. Their declared sizes are 23, 29, 31 and 37 respectively and do not enter the retained scoped quantity.

## Reproduce and validate

The evidence stores seven complete executable source files with SHA-256 hashes. `Extract-Evidence.ps1` requires a new external directory, verifies each captured byte sequence, and writes only simple filenames within it. `Run-Experiment.ps1` requires the assigned worktree's executable inputs to match the pinned base and loads that worktree's Release build. Emulator credentials are supplied through `OPS1060_COSMOS_KEY` and `OPS1060_BLOB_KEY`; their values and connection strings are absent from committed evidence.

Start only the named `grace-ops-1060-cosmos` and `grace-ops-1060-azurite` fixtures on free loopback ports 18083 and 11002. The startup script rejects existing names or occupied ports and uses cached images with `--pull never`. When replaying while those fixtures are already running, use their existing owned providers and a fresh fixture suffix. Do not start shared Aspire or touch another task's containers.

PowerShell, after extracting the scripts and setting local emulator credential inputs:

```powershell
./Run-Experiment.ps1 -RepositoryRoot 'C:/Source/Grace-worktrees/issue-1060-artifact-measurement-discovery' -RunLabel original -FixtureSuffix 1066
./Extract-Evidence.ps1 -EvidencePath '<repository>/docs/design/Operations.Artifact-Measurement-Experiment.json' -Destination '<new-external-replay-directory>'
& '<new-external-replay-directory>/Run-Experiment.ps1' -RepositoryRoot 'C:/Source/Grace-worktrees/issue-1060-artifact-measurement-discovery' -RunLabel replay -FixtureSuffix 1067 -SkipBuild
```

bash / zsh, invoking the same supported PowerShell scripts:

```bash
pwsh -File ./Run-Experiment.ps1 -RepositoryRoot '<assigned-worktree>' -RunLabel original -FixtureSuffix 1066
pwsh -File ./Extract-Evidence.ps1 -EvidencePath '<repository>/docs/design/Operations.Artifact-Measurement-Experiment.json' -Destination '<new-external-replay-directory>'
pwsh -File '<new-external-replay-directory>/Run-Experiment.ps1' -RepositoryRoot '<assigned-worktree>' -RunLabel replay -FixtureSuffix 1067 -SkipBuild
```

Use unused suffixes if a previous run already owns those repository fixtures. No recursive cleanup is performed. The stop script captures logs and stops only the exact two owned containers, retaining their data.

The matching Release build passed with zero warnings and errors. Original and clean extracted replay each passed **34 assertions**, using fresh repository suffixes 1066 and 1067. All seven executable source hashes and generated assembly-load script hashes match; the independent identity ledger rows match exactly. Original retained-state enumeration consumed five Cosmos pages with eight documents/events and 12,946 response bytes; object enumeration consumed three pages with six objects and 59 payload bytes. These are fixture observations, not throughput measurements.

Fourteen relevant source dependency hashes and 137 build-output DLL hashes were checked against the assigned worktree after replay. All captured JSON and byte/text hashes parse and match; all five PowerShell scripts parse; Markdown lint reported both actual owned Markdown files and zero errors. `git diff --check` passed. Required final-head GitHub Validate, independent R1 and Shape Review remain controller-owned. No local Fast/Full, shared hosted suite, cloud deployment or production algorithm is claimed.

## Smallest next recommendation

The next SystemAdmin diagnostic is **retained Artifact declared bytes**. It enumerates retained Artifact state through the existing storage-provider selection, projects each stream, binds owner/organization/repository, counts each Artifact once, and reports only a finished observation window. Keep declared bytes and distinct Artifact count together, including zero. State explicitly that missing objects, cleanup residue and unattached Artifacts affect coverage differently.

The experiment justifies that source meaning; it does not implement the diagnostic contract. PR #1063 owns producer validation, scope checks and caller-cancellable enumeration under the accepted no-arbitrary-cap direction. Observed Artifact-prefix object bytes remain a separate investigation. Defer repository totals, usage permissions, scheduling, durable observations, billing, metadata repair, automatic cleanup/recovery, Library coverage and generalized measurement abstractions.
