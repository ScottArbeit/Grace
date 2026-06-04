# Grace Rust SDK proof lane

The Rust SDK lane is a generator-feasibility proof lane, not a supported public SDK facade yet.

## Status

- Package facade harness: implemented proof harness.
  `cargo test` passes for metadata exposure in `sdk/rust/grace-sdk`.
- Raw OpenAPI Generator crate: accepted proof with guardrails.
  OpenAPI Generator `rust` output passes `cargo check`.
- Supported Rust facade behavior: deferred.
  No facade operations, auth policy, retries, or protocol-vector tests exist yet.
- Stable raw generated crate: rejected.
  Generated crate names and module layout are prototype evidence only.

Rust feasibility is positive at `cargo check` level only. The broad generated crate is useful evidence for a future
hidden raw-client implementation, but it is not a publishing path and should not be documented as Grace's public Rust
API.

## Proof commands

Run from the repository root unless noted.

PowerShell:

```powershell
Push-Location ./sdk/generated/matrix/openapi-generator/rust
cargo check
Pop-Location

Push-Location ./sdk/rust/grace-sdk
cargo test
Pop-Location
```

bash / zsh:

```bash
(
  cd ./sdk/generated/matrix/openapi-generator/rust
  cargo check
)

(
  cd ./sdk/rust/grace-sdk
  cargo test
)
```

## Guardrails

- Keep the raw generated crate behind a future handwritten facade.
- Do not publish `grace_generated_openapi_probe` as the supported Rust crate.
- Do not hand-edit generated output to make `cargo check` pass.
- Keep the `--skip-validate-spec` dependency visible until the OpenAPI projection is strict-generator clean.
