# Grace Python SDK proof lane

The Python SDK lane is a proof lane, not a supported public SDK facade yet.

## Status

- Package facade harness: implemented proof harness.
  `python ./smoke/import_facade.py` imports `grace_sdk.GraceClient`.
- Raw generated client: generator feasibility proven, prototype output retired.
  Issue #221 showed that OpenAPI Generator `python` output imports after an editable install, with spec validation
  skipped. Issue #1089 removed that prototype output from the repository.
- Supported Python facade behavior: deferred.
  No facade operations, auth policy, retries, or protocol-vector tests exist yet.
- Stable raw generated package: rejected.

The package root under `sdk/python/grace-sdk` exists so Grace can prove extraction-compatible package boundaries and
facade imports. It intentionally does not expose generated API/model modules as the stable public surface.

## Proof commands

Run from the repository root.

PowerShell:

```powershell
python ./sdk/python/grace-sdk/smoke/import_facade.py
```

bash / zsh:

```bash
python ./sdk/python/grace-sdk/smoke/import_facade.py
```

## Guardrails

- Keep any future raw generated client behind the handwritten facade.
- Do not hand-edit generated output to make imports pass.
- A future raw client generated with OpenAPI Generator still depends on `--skip-validate-spec` until the OpenAPI
  projection is strict-generator clean.
