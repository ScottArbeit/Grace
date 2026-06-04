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
  reviewable change. This scaffold does not accept generated clients or derived OpenAPI artifacts yet. If future work
  declares generated artifacts in the manifest before adding a real verifier, the gate remains pending/failing.
- `Quality` scans represented operations for missing or duplicate `operationId` values, missing operation tags,
  response blocks, 400/500 error response coverage, and reusable transport header components.
- `SdkPackage` is a placeholder gate. It remains pending/failing even if `sdk/package-proof.json` appears, until a
  future verifier validates package metadata, exported facade surface, import execution evidence, and generator
  isolation.
- `ProtocolVectors` is a placeholder gate. It remains pending/failing even if `test-vectors/protocol` appears, until a
  future verifier validates vector schema, required coverage, and parity execution results.

## Acceptance rules

Do not treat a generated artifact as fresh in this S01 scaffold. A later issue must add verifier support that
recomputes source digests from current canonical sources, validates generator/tool-version provenance, and rejects
hand-edited derived output before generated artifacts can pass. Do not describe OpenAPI as SDK-grade while the
`Quality` gate reports pending tag, header, or error-shape coverage. Do not claim SDK package acceptance from the
`SdkPackage` placeholder, and do not claim protocol parity from the `ProtocolVectors` placeholder.
