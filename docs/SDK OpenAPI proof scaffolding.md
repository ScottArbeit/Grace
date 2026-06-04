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
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check CanonicalVersion
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Projections
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Quality
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check SdkPackage
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check ProtocolVectors
```

bash / zsh:

```bash
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Freshness
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check CanonicalVersion
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Projections
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check Quality
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check SdkPackage
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check ProtocolVectors
```

Generate the canonical bundle and generator compatibility projection before updating OpenAPI artifacts:

```powershell
pwsh ./src/OpenAPI/generate-openapi-projections.ps1
```

bash / zsh:

```bash
pwsh ./src/OpenAPI/generate-openapi-projections.ps1
```

## What the scaffold proves

- `Freshness` checks `src/OpenAPI/OpenAPI.ProofManifest.json` against every canonical
  `src/OpenAPI/*.OpenAPI.yaml` file by SHA-256 hash. OpenAPI source changes must update the manifest in the same
  reviewable change. It also checks every declared derived artifact hash and source digest, so hand edits to derived
  artifacts or canonical source edits without regeneration fail the gate.
- `CanonicalVersion` checks the canonical OpenAPI entrypoint and manifest for OpenAPI 3.2.0.
- `Projections` checks the generated canonical bundle, the OpenAPI 3.1.2 generator compatibility projection, and the
  projection loss report. The current projection is intentionally narrow: it changes only the root `openapi` version
  line from 3.2.0 to 3.1.2 and records that loss for generator compatibility.
- `Quality` scans represented operations for missing or duplicate `operationId` values, missing operation tags,
  response blocks, 400/500 error response coverage, and reusable transport header components.
- `SdkPackage` is a placeholder gate. It remains pending/failing even if `sdk/package-proof.json` appears, until a
  future verifier validates package metadata, exported facade surface, import execution evidence, and generator
  isolation.
- `ProtocolVectors` is a placeholder gate. It remains pending/failing even if `test-vectors/protocol` appears, until a
  future verifier validates vector schema, required coverage, and parity execution results.

## Acceptance rules

Do not edit `src/OpenAPI/Grace.OpenAPI.yaml`, `src/OpenAPI/Grace.OpenAPI.3.1.2.yaml`, or
`src/OpenAPI/Grace.OpenAPI.3.1.2.loss-report.json` by hand. Change canonical source, then run
`pwsh ./src/OpenAPI/generate-openapi-projections.ps1` so the bundle, projection, loss report, and proof manifest are
refreshed together. Do not describe OpenAPI as SDK-grade while the `Quality` gate reports pending tag, header, or
error-shape coverage. Do not claim SDK package acceptance from the `SdkPackage` placeholder, and do not claim protocol
parity from the `ProtocolVectors` placeholder.
