# SDK OpenAPI proof scaffolding

Issue #212 adds deterministic proof scaffolding for the SDK/OpenAPI epic without accepting a generator, SDK tier, or
protocol implementation. The scaffold is intentionally narrow: it proves represented OpenAPI source freshness, scans
contract quality risk points, and records package/protocol proof gaps as pending gates.

## Commands

Run the full scaffold with pending gates allowed:

```powershell
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending
```

Run individual gates when a later issue is ready to turn a placeholder into a hard proof:

```powershell
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Freshness
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Quality
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check SdkPackage
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check ProtocolVectors
```

bash / zsh:

```bash
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Freshness
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Quality
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check SdkPackage
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check ProtocolVectors
```

## What the scaffold proves

- `Freshness` checks `src/OpenAPI/OpenAPI.ProofManifest.json` against every canonical
  `src/OpenAPI/*.OpenAPI.yaml` file by SHA-256 hash. OpenAPI source changes must update the manifest in the same
  reviewable change. This gate accepts no generated client or derived OpenAPI artifact unless the manifest declares it
  with artifact hash and source digest evidence.
- `Quality` scans represented operations for missing or duplicate `operationId` values, missing operation tags,
  response blocks, 400/500 error response coverage, and reusable transport header components.
- `SdkPackage` is a placeholder gate. Until package proof metadata exists, it reports that no SDK package export/import
  proof exists and no SDK package or tier is accepted.
- `ProtocolVectors` is a placeholder gate. Until `test-vectors/protocol` exists with vector files, it reports that no
  protocol vector suite exists and no Tier 3 or Tier 4 protocol parity is accepted.

## Acceptance rules

Do not treat a generated artifact as fresh unless it is declared in `OpenAPI.ProofManifest.json` with artifact hash and
source digest evidence. Do not describe OpenAPI as SDK-grade while the `Quality` gate reports pending tag, header, or
error-shape coverage. Do not claim SDK package acceptance from the `SdkPackage` placeholder, and do not claim protocol
parity from the `ProtocolVectors` placeholder.
