# SDK OpenAPI Final Audit

Issue #228 closes the SDK/OpenAPI epic evidence ledger for the `epic/211-sdk-openapi` integration branch. It records
what the branch currently proves, what is intentionally deferred, and where claims must stay narrow for the final
epic-to-`main` release candidate.

## Audit Scope

- Parent epic: [#211](https://github.com/ScottArbeit/Grace/issues/211).
- Final audit issue: [#228](https://github.com/ScottArbeit/Grace/issues/228).
- Audited integration commit: `dd46206f514b40c88eba328f256ad4036fc04da0`.
- Merge target for this slice: `epic/211-sdk-openapi`.
- Primary design spec: [SDK and OpenAPI architecture specification](SDK%20and%20OpenAPI%20architecture.md).
- Raw-client ADR: [ADR 0003](adr/0003-generated-raw-sdk-clients-behind-facades.md).

Status terms in this document use the completion vocabulary from the architecture specification:

- `Implemented and proven`: current branch behavior has runnable evidence or committed proof artifacts.
- `Implemented with incomplete proof`: current branch has implementation or docs, but one or more proof gates remain
  pending or were not rerun in this final docs slice.
- `Intentionally deferred`: the epic keeps the item as future work without claiming support now.
- `Explicitly out of scope`: the item is outside this first SDK/OpenAPI milestone.
- `Rejected with rationale`: the branch deliberately does not accept the item as a design or release claim.

## Release And Compatibility Matrix

| Surface | Current branch status | Compatibility claim | Evidence | Residual risk |
| ------- | --------------------- | ------------------- | -------- | ------------- |
| OpenAPI canonical source | Implemented with incomplete proof | Canonical OpenAPI source targets 3.2.0; SDK-grade OpenAPI is not fully claimed because quality gates still report pending tag and error-response coverage. | `pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending` passed freshness, canonical version, projections, and operation scan with four pending gates. | Storage operations still lack accepted tag coverage; `/openApi` still lacks accepted 400/500 coverage in the scaffold. |
| OpenAPI 3.1.2 generator projection | Implemented and proven | Projection is a scripted, loss-aware derived artifact for generator compatibility only. | `src/OpenAPI/Grace.OpenAPI.3.1.2.yaml`, `src/OpenAPI/Grace.OpenAPI.3.1.2.loss-report.json`, and `src/OpenAPI/OpenAPI.ProofManifest.json`; `prove-openapi.ps1` projection gate passed. | The projection is not a substitute for strict generator validation; OpenAPI Generator still needs `--skip-validate-spec`. |
| Raw generated clients | Implemented and proven as proof artifacts | OpenAPI Generator TypeScript, Python, and Rust output is accepted with guardrails behind facades; Kiota and NSwag are rejected for the current projection. | `pwsh ./sdk/scripts/invoke-generator-matrix.ps1`; `sdk/generated/matrix/generator-matrix-evidence.json`; `sdk/generated/matrix/generator-matrix-report.md`. | Accepted OpenAPI Generator proof depends on skipped spec validation until schema-shape debt is fixed. |
| Handwritten facade boundary | Implemented and proven | Public SDK usage is facade-first. Raw generated clients are implementation/proof artifacts, not the primary SDK surface. | `pwsh ./sdk/scripts/test-sdk-packages.ps1`; `npm test` in `sdk/typescript/grace`; package READMEs and ADR 0003. | Language package internals can still be importable by determined users, so docs and exports must keep the public contract clear. |
| .NET SDK harness | Implemented and proven as harness | .NET package lane builds and exposes generated contract metadata behind a facade harness. | `pwsh ./sdk/scripts/test-sdk-packages.ps1` ran `dotnet build --nologo` for `sdk/dotnet/Grace.Sdk.Harness`. | This is a harness lane, not a broad new .NET SDK feature set. |
| TypeScript Node SDK | Implemented and proven through narrow claims | Node-focused TypeScript facade supports Tier 1 behavior, Tier 2 whole-file transfer, Tier 3 protocol vectors, and the S15 Tier 4 capability `facade-transfer-progress-diagnostics` only. Browser TypeScript is not claimed. | `sdk/typescript/grace/package.json`; `npm test` passed 18 tests, including facade policy, Tier 2 transfer, protocol vectors, and progress diagnostics. | Full Tier 4 is not claimed: manifest upload, ContentBlock transfer, direct storage dedupe, resumability, and offline watch remain non-claims. |
| Python SDK lane | Implemented with incomplete proof | Python has a package facade harness and raw generated-client feasibility proof. It is not a supported productized Python SDK. | `sdk/python/proof/s16-python-openapi-generator-proof.json`; `pwsh ./sdk/scripts/test-sdk-packages.ps1` ran Python facade import smoke. | Supported Python facade operations, protocol vectors, and package release policy remain deferred. |
| Rust SDK lane | Implemented with incomplete proof | Rust has a package facade harness and raw generated-client feasibility proof at `cargo check` level. It is not a supported productized Rust SDK. | `sdk/rust/proof/s16-rust-openapi-generator-proof.json`; `pwsh ./sdk/scripts/test-sdk-packages.ps1` ran `cargo test`. | Supported Rust facade operations and protocol vectors remain deferred. |
| Grace Protocol docs and vectors | Implemented and proven for current TypeScript Tier 3 claim | Protocol v1 docs and vectors cover content addresses, compact ContentBlock payloads, manifest validation, corruption rejection, and manifest eligibility boundaries. | `docs/protocol/**`, `test-vectors/protocol/**`, TypeScript `npm test`; `Grace.Types.Tests` vector proof is documented by `test-vectors/protocol/README.md`. | The OpenAPI scaffold's `ProtocolVectors` placeholder still reports pending; the newer TypeScript and vector evidence must be cited directly. |
| SDK lifecycle policy | Implemented with incomplete final-slice proof | Server, .NET SDK, CLI, and TypeScript facade paths have lifecycle warning/error handling from prior merged slices. | Epic history includes #218, #219, and #222; TypeScript lifecycle handling was rerun by `npm test`. | S17 did not rerun a broad server/.NET validation gate. |

## Final Evidence Run

These commands were run from `C:\Source\Grace-gh-228` on the final audit branch unless another working directory is
shown.

| Command | Outcome | Notes |
| ------- | ------- | ----- |
| `git fetch origin` | Passed | Refreshed remote refs before final audit work. |
| `pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending` | Passed with pending gates | Freshness, canonical version, projection, and operation scan passed. Pending gates: 13 storage operation tags, `/openApi` 400/500 coverage, SDK package placeholder, protocol vector placeholder. |
| `pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check` | Passed | Deterministic generated metadata freshness passed for the facade harness. |
| `pwsh ./sdk/scripts/test-sdk-packages.ps1` | Passed | TypeScript facade smoke, Python facade smoke, .NET harness build, and Rust facade test passed. |
| `npm test` in `sdk/typescript/grace` | Passed on rerun | 18 Node tests passed. The first parallel attempt failed while dependencies were not yet installed; the stable rerun after package smoke passed. |
| `pwsh ./sdk/scripts/invoke-generator-matrix.ps1` | Passed | Refreshed generator matrix evidence without tracked diff. Accepted proof points remained OpenAPI Generator TypeScript, Python, and Rust with guardrails. |

Broad `pwsh ./scripts/validate.ps1 -Fast` was not run for this docs-only closure slice. The final runtime/server gate
should run on the final epic-to-`main` release-candidate PR after review refreshes the epic branch against
`origin/main`.

## Requirement Traceability

| ID | Final status | Evidence and rationale |
| -- | ------------ | ---------------------- |
| SDK-001 | Implemented with incomplete proof | OpenAPI is the documented source of HTTP truth and freshness/projection gates pass, but SDK-grade quality is not fully claimed while pending tag and error-response gates remain. |
| SDK-002 | Implemented and proven | ADR 0003, SDK README, package exports, and smoke tests keep generated raw clients behind facades. |
| SDK-003 | Implemented and proven | Facade-first package surfaces are documented and smoke-tested; raw generated package shape is not a compatibility promise. |
| SDK-004 | Implemented and proven | The epic issue, package metadata, and this audit use tiers only as maturity gates and record non-claims. |
| SDK-005 | Implemented and proven | TypeScript Tier 2 whole-file upload/download facade tests pass in `npm test`. |
| SDK-006 | Implemented and proven | Protocol docs and golden vectors exist; TypeScript protocol vector tests pass. |
| SDK-007 | Implemented with incomplete proof | Only the S15 `facade-transfer-progress-diagnostics` Tier 4 capability is proven. Broader Tier 4 behaviors are not claimed. |
| SDK-008 | Implemented and proven | TypeScript Node is the implemented non-.NET SDK target and declares a native Node runtime boundary. |
| SDK-009 | Explicitly out of scope | Browser TypeScript remains out of scope for this milestone and is not claimed by package metadata or docs. |
| SDK-010 | Intentionally deferred | Python has feasibility and facade import proof only; supported Python SDK behavior is deferred. |
| SDK-011 | Implemented and proven | Rust OpenAPI Generator feasibility is recorded by the matrix and S16 proof artifact. |
| SDK-012 | Implemented and proven | `prove-openapi.ps1` canonical version gate passes for OpenAPI 3.2.0. |
| SDK-013 | Implemented and proven | The 3.1.2 projection and loss report are generated and checked by the projection gate. |
| SDK-014 | Implemented and proven | The OpenAPI quality scan checked 102 operations and did not report operation ID failures. |
| SDK-015 | Implemented with incomplete proof | Root and operation tags exist broadly, but the quality scan still reports 13 storage operations with pending tag coverage. |
| SDK-016 | Implemented with incomplete proof | Response/media/error modeling improved across the epic, but the quality scan still reports `/openApi` missing 400/500 coverage. |
| SDK-017 | Implemented and proven | Transport header components are part of the OpenAPI quality scan and no header gate failure was reported. |
| SDK-018 | Implemented and proven | Package lanes use language-native package metadata and pre-release versions. |
| SDK-019 | Implemented and proven | API contract versioning is date-based in OpenAPI/generator metadata and facade smoke output reports `2023-10-01`. |
| SDK-020 | Implemented and proven | TypeScript tests prove the facade sends a pinned default API version. Other lanes expose metadata harnesses only. |
| SDK-021 | Intentionally deferred | Multi-version facade support is not required for this milestone and is not claimed. |
| SDK-022 | Implemented with incomplete final-slice proof | Server and SDK lifecycle slices are merged; TypeScript lifecycle parsing was rerun. S17 did not rerun server lifecycle tests. |
| SDK-023 | Implemented with incomplete final-slice proof | Unsupported-client handling was implemented in prior lifecycle slices and TypeScript handling was rerun. S17 did not rerun server lifecycle tests. |
| SDK-024 | Intentionally deferred | A compatibility/status endpoint remains optional diagnostic future work. |
| SDK-025 | Implemented and proven | Raw generated clients are not published as stable packages and remain compatibility-limited proof artifacts. |
| SDK-026 | Implemented and proven | Generator selection is guarded by the matrix and final audit rather than accepted from route existence alone. |
| SDK-027 | Implemented and proven | Kiota, OpenAPI Generator, and NSwag comparison points are recorded in the generator matrix. |
| SDK-028 | Implemented and proven | `sdk/`, `docs/protocol/`, and `test-vectors/protocol/` exist and are package/extraction-oriented. |
| SDK-029 | Implemented with incomplete proof | OpenAPI examples and docs exist, but this slice did not run a dedicated schema-example validation command. |
| SDK-030 | Implemented and proven | This final audit records the completion evidence, matrix, traceability, deferrals, and residual risks. |

## Testing Requirement Traceability

| ID | Final status | Evidence and rationale |
| -- | ------------ | ---------------------- |
| T-001 | Implemented and proven | `prove-openapi.ps1 -Check All -AllowPending` passed canonical OpenAPI 3.2.0 verification. |
| T-002 | Implemented and proven | The same command passed source and derived artifact freshness checks. |
| T-003 | Implemented and proven | The OpenAPI quality scan reported no operation ID failure across 102 operations. |
| T-004 | Implemented with incomplete proof | The quality scan still reports 13 storage operation tag entries as pending. |
| T-005 | Implemented with incomplete proof | Error-shape work exists, but `/openApi` 400/500 coverage remains pending in the quality scan. |
| T-006 | Implemented and proven | TypeScript Tier 2 download tests prove raw text URI handling through the facade; storage OpenAPI was covered by prior S04 work. |
| T-007 | Implemented and proven | TypeScript tests prove pinned default `X-Api-Version` request policy. |
| T-008 | Implemented and proven | TypeScript tests prove Node client identity headers; prior lifecycle slices covered CLI/.NET identity. |
| T-009 | Implemented with incomplete final-slice proof | Server deprecated-client lifecycle behavior was implemented in S07; S17 did not rerun server tests. |
| T-010 | Implemented and proven for TypeScript; incomplete final-slice proof elsewhere | TypeScript tests prove lifecycle warning/error parsing. CLI/.NET lifecycle handling is prior merged evidence. |
| T-011 | Implemented with incomplete final-slice proof | Unsupported-client server behavior was implemented in S07; S17 did not rerun server tests. |
| T-012 | Implemented and proven for TypeScript; incomplete final-slice proof elsewhere | TypeScript tests prove unsupported-client details are preserved as `GraceError`. CLI/.NET UX is prior merged evidence. |
| T-013 | Implemented and proven | `generate-sdk-clients.ps1 -Mode Check` and `invoke-generator-matrix.ps1` passed deterministic evidence checks. |
| T-014 | Implemented and proven | Package smoke tests and generator matrix compile/import probes passed for accepted proof points. |
| T-015 | Implemented and proven | TypeScript facade tests prove facade-first request policy and public exports. |
| T-016 | Implemented and proven | TypeScript Tier 2 upload/download tests passed through the facade. |
| T-017 | Implemented and proven | TypeScript content address and ContentBlock vector tests passed. |
| T-018 | Implemented and proven | TypeScript manifest reconstruction and corruption rejection vector tests passed. |
| T-019 | Implemented and proven | TypeScript eligibility vector tests passed for the default Tier 3 boundary. |
| T-020 | Implemented and proven | Generator matrix and S16 Rust proof record `cargo check`; package smoke ran Rust facade `cargo test`. |
| T-021 | Implemented with incomplete proof | OpenAPI examples exist, but this slice did not run a dedicated schema-example validator. |
| T-022 | Implemented and proven | Package exports and smoke tests keep documented primary usage on facades, not raw generated clients. |

## Explicit Non-Claims

- This branch does not claim browser TypeScript support.
- This branch does not claim a full TypeScript Tier 4 SDK. It claims only
  `facade-transfer-progress-diagnostics` beyond Tier 3.
- This branch does not claim supported productized Python or Rust SDKs.
- This branch does not publish stable standalone raw generated-client packages.
- This branch does not claim Kiota or NSwag acceptance for the current OpenAPI projection.
- This branch does not claim strict OpenAPI Generator validation; accepted OpenAPI Generator proof points still use
  `--skip-validate-spec`.
- This branch does not claim SDK-grade OpenAPI completion while the proof scaffold has pending tag and error-response
  gates.

## Follow-Up Candidates

- Close the remaining OpenAPI quality pending gates: storage operation tag acceptance and `/openApi` 400/500 response
  coverage.
- Replace the scaffold placeholder `SdkPackage` and `ProtocolVectors` gates with verifiers that consume the newer
  package smoke and vector evidence directly.
- Fix the projection schema-shape debt that forces OpenAPI Generator to run with `--skip-validate-spec`.
- Decide whether Python or Rust should graduate from proof harnesses to supported facade milestones.
- Create a separate browser TypeScript design issue before making any browser package or runtime claim.
