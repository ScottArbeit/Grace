# Library storage experiment

Date: 2026-09-06 Pacific time. Outcome: the registered-provider and paused-snapshot approach passed the tests below. The synthetic acceptance/recovery model passed 34 cases.

This is preparation for Issue #1042. It does not implement the replacement service or certify the existing PRs. [Libraries design](../Libraries.Design.md) remains the accepted contract.

## Question and supported world

Can one non-reentrant repository grain manage bounded records through the registered Orleans Cosmos provider, recover writes using fresh state, and build a complete snapshot with direct Cosmos SQL reads while holding its turn?

The experiment uses a real local Orleans silo and Cosmos emulator, the approved `Microsoft.Orleans.Persistence.Cosmos 10.2.2-hpk.1` package, one repository grain and six named providers. Record writes and clears occur through `IGrainStorage` inside that grain. Direct Cosmos setup creates the test database/containers; application-level Cosmos operations are reads and queries.

The repository-state fixture is separate from production data. The experiment is at `C:\Users\scott\Documents\Codex\Reviews\library-redesign-1042\prototype`. Its source archive is `C:\Users\scott\Documents\Codex\Reviews\library-redesign-1042\library-redesign-prototype.zip`.

## Inputs and provenance

| Input | Pinned value |
| --- | --- |
| Grace reference base | `975ec65b188702d5cb7de3b2220e589d4957b565` |
| Orleans package | `Microsoft.Orleans.Persistence.Cosmos 10.2.2-hpk.1` |
| Package source | `12c6f7ea413eede43c1f1a589b51430fcf9ae7ed` |
| Package SHA-256 | `E930D0461935120313EEF0D41CD38AE808993A2DF3CBF10EFFDD64571D384575` |
| SDK | .NET 10.0.400 on Windows |
| Emulator image ID | `sha256:6ce44ed4ac69cf69a6cb7982d0536cfaa22696172168fc9fe9a722a189d38f99` |
| Isolated container/database | `grace-library-redesign-1042-cosmos` / `library-redesign-1042` |
| Fixture repository | `run-20260907051313` |
| Source archive SHA-256 | `2F931BA4C6BBEBCC38B338C7DE0AE2F1907595FFAAC6621BF9683EA43768450C` |

[Machine-readable results](Libraries.Storage-Experiment.json) contain the case names and raw observations. The local archive contains the C# host/provider experiment, F# record and model fixtures, package configuration and PowerShell runner. The experiment shell is not intended for merge into production.

## Real provider and serialization results

All six providers passed create, read, conditional replace, stale-ETag rejection, duplicate-create rejection, an ambiguous completed write followed by a fresh read, and clear. The layouts used one, two and three ordered key components as required.

The stale writer retained a changed working buffer. A new provider read returned the winning stored value, demonstrating the required separation between failed working state and stored state. The ambiguous-write case threw immediately after a real provider write completed, then discarded the working state and recovered the stored result.

Nested F# records and options survived a real Cosmos round trip. The JSON options reproduce Grace's relevant F# converter settings. The fixture does not exercise NodaTime, every production DTO, generated RPC codecs for those DTOs or the complete server serializer graph.

The initially chosen `NOT IS_DEFINED(c.State.Tombstone)` query returned zero records. Inspection showed `Tombstone: null`. The corrected `IS_NULL(...)` query returned the expected rows. The maintained design now records this observed encoding.

The emulator rejected the provider-created container/index setup with a PostgreSQL syntax error. The successful experiment pre-provisions normal containers before silo startup, matching Grace's Aspire setup, and leaves provider resource creation disabled. This is a setup result, not a change to the record-write design.

## Full silo restart and snapshot results

The experiment wrote 100,000 synthetic F# item records through the repository grain, with 16 independent writes in flight per batch. Seeding took 137.7 seconds; that is separate from baseline build time.

Both the shared-client query and a query after stopping and disposing the complete silo/client and starting new ones returned exactly 100,000 records. The fresh query client point-read the last item before enumeration. This tests the local emulator path; it does not establish every Azure replication or partition-split behavior.

An interrupted build wrote its first shard and threw. No manifest existed. A fresh silo retried the same test identity, checked its existing shard, completed the remaining shards and published the manifest. A second forced build exercised the same pause at another test identity. A concurrent request wrote its marker only after that second manifest existed.

| Measurement | Resumed build | Second build |
| --- | --- | --- |
| Item records | 100,000 | 100,000 |
| Shards | 100 | 100 |
| Largest serialized shard | 262,482 bytes | 262,482 bytes |
| Total serialized shard bytes | 26,246,675 | 26,246,675 |
| Elapsed build time | 20.76 seconds | 20.53 seconds |
| Managed memory increase observed | 382,357,920 bytes | 322,633,936 bytes |

Every stored shard was reread and its BLAKE3 hash checked. Every shard was below the 1,000,000-byte limit. The concurrent-write case uses an explicit task gate, not a timing sleep.

Memory figures are sampled differences, not peak working-set measurements. The fixture allocates lists and repeated serializer options for convenience. Its items have short synthetic names and a subset of production fields. These are feasibility measurements, not maximum-payload capacity or release latency promises. Emulator request-unit values are not used to estimate Azure cost.

## Synthetic protocol model

The F# model saves one snapshot after each named effect. Restart reloads that snapshot; the interrupted working state is discarded. The snapshot represents separate logical records. It does not assert that Cosmos provides a transaction across those records.

The 34 cases cover:

- Before and after each of ten acceptance effects: reservation, accepted change, tracked reference add, workflow completion, item, old slot, new slot, receipt, tracked-add acknowledgment and control completion.
- Repeated retry after recovery and rejection of a different request hash under the same operation ID.
- Before and after history segment, history tail, history progress, send progress and failed-envelope clear.
- A send accepted before progress was stored; recovery resumes from the saved position.
- Repeated broker failure while history completes, retaining one stable failed envelope.
- A stale edit revision choosing a deterministic free conflict sibling, including an occupied first candidate and a current-revision control.

The model's terminal checks require cleared pending work, one accepted change, matching receipt/projections, completed synthetic workflow and exactly one content contribution. A receipt while pending remains is not exposed as terminal.

Counter, workflow and broker operations are modeled. The experiment did not run the production content-counter actor, real manifest activation, Redis loss or a live Service Bus outage. Existing PR #1043 tests remain evidence for those unchanged components; the replacement must preserve their operation identities and order and test their actual composition.

## Implementation consequence and remaining checks

The experiment supports keeping one repository actor, typed provider functions, direct SQL queries, a paused baseline build, compact history and notification progress. It found no need for record actors or another production type.

The next Issue #1042 run can implement the first remote vertical slice under a new charter. Its first integration test must use the actual upload, retained-content, counter/workflow, acceptance, signed-read and restart path. Preserve the successful component tests from PR #1043 selectively. Stop if composition requires changing the accepted sequence or adding another owner/lifecycle.

Before the remote replacement is merge-ready, also complete:

- Full production record and RPC serialization, nested query paths, field IDs and generated clients.
- Concurrent/stale activation handling beyond the stale-ETag storage case.
- Directory cycles, moves, catalog/slot revalidation and real repeated-content conflicts.
- Actual history segment/tail and notification recovery using the implemented functions.
- Production baseline identity/reuse, public token paging, tombstones, cancellation, full byte limits and measured memory/latency.
- Current-revision GitHub Validate and independent review.

Issue #1039 must separately test the redesigned three-table SQLite model with real filesystem publication and the existing two-copy fixture. The 34 model cases do not substitute for that test.

## Reproduction

PowerShell:

```powershell
Set-Location C:\Users\scott\Documents\Codex\Reviews\library-redesign-1042\prototype
pwsh -File ./Run.ps1
```

bash / zsh with PowerShell installed:

```bash
cd /c/Users/scott/Documents/Codex/Reviews/library-redesign-1042/prototype
pwsh -File ./Run.ps1
```

`Run.ps1 -ReuseFixture` repeats the snapshot and protocol cases against the existing seeded emulator. The script was parser-checked; its underlying build and experiment commands were executed directly. The local emulator key is Microsoft's documented public test key, and the connection is restricted to a fixed loopback endpoint.
