# Grace Rust SDK proof lane

The Rust SDK lane is a proof lane, not a supported public SDK facade yet.

## Status

- Package facade harness: implemented proof harness.
  `cargo test` passes for metadata exposure in `sdk/rust/grace-sdk`.
- Raw generated crate: generator feasibility proven, prototype output retired.
  Issue #221 showed that OpenAPI Generator `rust` output passes `cargo check`, with spec validation skipped. Issue
  #1089 removed that prototype output from the repository.
- Supported Rust facade behavior: deferred.
  No facade operations, auth policy, retries, or protocol-vector tests exist yet.
- Stable raw generated crate: rejected.

Rust feasibility is positive at `cargo check` level only. That's enough evidence for a future hidden raw-client
implementation, but it is not a publishing path and should not be documented as Grace's public Rust API.

## Proof commands

Run from the repository root.

PowerShell:

```powershell
Push-Location ./sdk/rust/grace-sdk
cargo test
Pop-Location
```

bash / zsh:

```bash
(
  cd ./sdk/rust/grace-sdk
  cargo test
)
```

## Guardrails

- Keep any future raw generated crate behind the handwritten facade.
- Do not hand-edit generated output to make `cargo check` pass.
- A future raw crate generated with OpenAPI Generator still depends on `--skip-validate-spec` until the OpenAPI
  projection is strict-generator clean.
