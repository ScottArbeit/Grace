# Committed Library declaration preflight

Question: can the existing Library provider read a complete accepted prefix and its immutable declarations without invoking repair or writing diagnostic state?

Issue: [Issue #1077](https://github.com/ScottArbeit/Grace/issues/1077), child of [Issue #554](https://github.com/ScottArbeit/Grace/issues/554). The frozen base is `8b418ac04e154b129809f673a2be2eecc4c6558e`. The [storage gate](https://github.com/ScottArbeit/Grace/issues/1077#issuecomment-5599170819) passed before production edits. The [JSON evidence](Operations.LibraryContentSize-Preflight.json) records the bounded results, with reproducible [fixture source](Operations.LibraryContentSize-Preflight.fsx) and [runner](Operations.LibraryContentSize-Preflight.ps1).

## Actual dependencies and observed boundary

- Windows, .NET 10, local Cosmos vNext emulator image `6ce44ed4ac69`, isolated container `grace-issue1077-provider-preflight`, localhost port 18090. No shared Aspire resources were started or stopped.
- Actual `Microsoft.Orleans.Persistence.Cosmos 10.2.2-hpk.1`, current `GraceDocumentIdProvider`, and keyed `IGrainStorage` registrations for Control/Changes/Current at partition depths 1/2/2. Resource creation is disabled on the providers. The harness provisions its own disposable database and containers before exercising reads.
- The provider writes the actual `State`, `PartitionKey`, `PartitionKey2`, `id`, `GrainType` and ETag envelopes. Records 1 through 206 span cursor segments 0 and 1. Control captures epoch/B=205, public ReplayFloor=203 and Pending=206. Immutable declarations A=10, B=20, pending=9 and orphan=7 are stored through the provider.
- Exhaustion observed **13 actual Cosmos pages, 205 selected records, two segments, 30 bytes and two manifest identities**. Original provider documents and ETags were identical before and after the diagnostic read.
- A fresh Cosmos client and fresh provider wrappers reread the same prefix. Advancing the committed tail to 206 preserved the earlier selected prefix at 205. Exact cursor 200 removal, required mapping removal, pre/partial cancellation, absent control and missing source container yielded no successful quantity in the fixture.
- The compiled production adapter additionally captured/exhausted B=205, returned explicit zero with an existing valid zero control and all required containers, and rejected zero after the Changes container was removed. The final run completed **15 checks**.

## Applicability and limits

Provider initialization and registration occur directly in disposable DI. This is real provider IO, but the fixture seeds control and changes; it does not call `RepositoryLibraryActor` admission. The permanent record types and signed cursor helpers are current. Mutation of ReplayFloor and Pending is fixture setup, not proof that a running actor naturally reached those values. The source does not model blob presence, cloud consistency, production recovery or SQL/broker effects.

The original twelve-check preflight's zero assertion only verified persisted/read zero control; its epoch assertion only detected changed epoch. The three added production-adapter assertions are stronger for zero/source existence. The initial production replay correctly rejected an invalid zero fixture copied with nonzero background progress. The corrected fixture resets those fields; the final passing run alone supports the production zero-adapter claim. Hosted tests separately exercise a successful zero envelope and actual upload/create/edit/repeat/rename/delete admission across cursor 200. Their CI result and exact emitted envelopes must be recorded for the final PR head before claiming hosted acceptance.

The earlier eleven-case September 9 assessment used simplified outer envelopes, modeled B and one observed Cosmos page. Its overlap control remains illustrative: modeled DirectoryVersion/Library union 60 versus separate totals 70. It cannot establish a repository union producer or replace this actual-provider result.

## Reproduce

Use an independently owned emulator at `https://127.0.0.1:18090` and a new output directory outside the checkout. The runner builds current `Grace.Actors`, loads the exact production key provider, generates references and runs the retained F# fixture. It records actual inputs and output; it does not clean up another task's resources. The fixture creates a uniquely named database on that disposable emulator. The supplied emulator key is the public local-development key.

PowerShell:

```powershell
docker run -d --pull never --name grace-library-diagnostic-proof -p 127.0.0.1:18090:8081 6ce44ed4ac69 --protocol https
pwsh ./docs/design/Operations.LibraryContentSize-Preflight.ps1 -OutputDirectory C:/Temp/grace-library-diagnostic-proof
```

bash / zsh:

```bash
docker run -d --pull never --name grace-library-diagnostic-proof -p 127.0.0.1:18090:8081 6ce44ed4ac69 --protocol https
pwsh ./docs/design/Operations.LibraryContentSize-Preflight.ps1 -OutputDirectory /tmp/grace-library-diagnostic-proof
```

Wait for that exact container's endpoint to become ready before the runner. Its final output is `production-result.json`, `production-envelopes.jsonl`, `input-hashes.json` and `result.log`. Delete only the exact disposable container you created when finished. The retained script is evidence tooling, not a production diagnostic interface.
