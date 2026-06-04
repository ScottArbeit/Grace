# Grace SDK monorepo

This directory contains the extraction-compatible Grace SDK package workspace. It is intentionally facade-first:
generated raw clients are implementation inputs behind language-native package facades, not the supported public SDK
surface.

The initial S09 scaffold proves package boundaries, generator provenance, deterministic freshness, and import/export
smoke checks. It does not claim completed language SDK behavior, browser compatibility, protocol parity, or stable
standalone raw generated-client publishing.

## Layout

| Path | Purpose | Public surface status |
| ---- | ------- | --------------------- |
| `dotnet/Grace.Sdk.Harness` | .NET package-lane harness for future extraction. | Facade placeholder only. |
| `typescript/grace` | TypeScript Node package-lane harness. | Exports the package facade only. |
| `python/grace-sdk` | Python package-lane harness. | Exports the package facade only. |
| `rust/grace-sdk` | Rust package-lane harness. | Exports the package facade only. |
| `generated` | Provenance manifest and generator report shared by the package lanes. | Not a package surface. |
| `scripts` | Deterministic generator and smoke-test harnesses. | Maintainer tooling. |

Language package roots are separate so a future extraction can move each lane into its own repository or publishing
pipeline without colliding on root package files such as `package.json`, `pyproject.toml`, `Cargo.toml`, or `.csproj`
files.

## Generator harness

The generator harness consumes the current OpenAPI generator compatibility projection at
`src/OpenAPI/Grace.OpenAPI.3.1.2.yaml`. It writes generated provenance metadata and small raw-client metadata stubs
into each language lane. The stubs are generated artifacts with provenance headers; do not edit them by hand.

PowerShell:

```powershell
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Generate
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check
```

bash / zsh:

```bash
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Generate
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check
```

The harness pins the generator harness schema and records deterministic provenance in
`sdk/generated/generator-report.json`. Live local tool versions are printed to the console as diagnostics during
harness runs; they are not committed to the freshness report. The initial generator engine is `grace-sdk-harness`
version `0.1.0`: it records the OpenAPI projection provenance and creates deterministic generated metadata stubs, but
it does not claim a final OpenAPI code generator selection.

## Smoke tests

Run the package import/export smoke tests from the repository root:

PowerShell:

```powershell
pwsh ./sdk/scripts/test-sdk-packages.ps1
```

bash / zsh:

```bash
pwsh ./sdk/scripts/test-sdk-packages.ps1
```

The smoke tests verify that each package lane can load its facade and observe the generated contract metadata through
that facade. They do not document raw generated imports as primary usage, and they do not test browser compatibility.

## Package boundary rules

- Public examples should import the facade package entrypoint, not generated modules.
- Generated raw-client output must include provenance and pass `generate-sdk-clients.ps1 -Mode Check`.
- Generated raw-client modules may support diagnostics or future implementation work, but their names, models, and
  package shape are not stable compatibility promises.
- Do not hand-edit files marked as generated. Change the OpenAPI projection or harness template, then regenerate.
- Do not add browser support claims until a separate browser-targeted issue owns and proves that surface.
