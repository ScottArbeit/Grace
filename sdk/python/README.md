# Grace Python SDK proof lane

The Python SDK lane is a proof lane, not a supported public SDK facade yet.

## Status

- Package facade harness: implemented proof harness.
  `python ./smoke/import_facade.py` imports `grace_sdk.GraceClient`.
- Raw OpenAPI Generator client: accepted proof with guardrails.
  OpenAPI Generator `python` output imports after editable install.
- Supported Python facade behavior: deferred.
  No facade operations, auth policy, retries, or protocol-vector tests exist yet.
- Stable raw generated package: rejected.
  Generated module names and package layout are prototype evidence only.

The package root under `sdk/python/grace-sdk` exists so Grace can prove extraction-compatible package boundaries and
facade imports. It intentionally does not expose generated API/model modules as the stable public surface.

## Proof commands

Run from the repository root unless noted.

PowerShell:

```powershell
python ./sdk/python/grace-sdk/smoke/import_facade.py

$venv = Join-Path $env:TEMP 'grace-python-generated-proof'
if (Test-Path -LiteralPath $venv) { Remove-Item -LiteralPath $venv -Recurse -Force }
python -m venv $venv
$python = Join-Path $venv 'Scripts\python.exe'
Push-Location ./sdk/generated/matrix/openapi-generator/python
& $python -m pip install -e . --quiet
$probe = 'import grace_generated_openapi_probe; ' +
    'from grace_generated_openapi_probe.api_client import ApiClient; ' +
    'print(grace_generated_openapi_probe.__version__); print(ApiClient.__name__)'
& $python -c $probe
Pop-Location
Remove-Item -LiteralPath $venv -Recurse -Force
```

bash / zsh:

```bash
python ./sdk/python/grace-sdk/smoke/import_facade.py

venv="${TMPDIR:-/tmp}/grace-python-generated-proof"
rm -rf "$venv"
python -m venv "$venv"
python_bin="$venv/bin/python"
cd ./sdk/generated/matrix/openapi-generator/python
"$python_bin" -m pip install -e . --quiet
probe='import grace_generated_openapi_probe; from grace_generated_openapi_probe.api_client import ApiClient; '
probe+='print(grace_generated_openapi_probe.__version__); print(ApiClient.__name__)'
"$python_bin" -c "$probe"
cd -
rm -rf "$venv"
```

## Guardrails

- Keep raw generated clients behind a future handwritten facade.
- Do not publish `grace_generated_openapi_probe` as the supported Python package.
- Do not hand-edit generated output to make imports pass.
- Keep the `--skip-validate-spec` dependency visible until the OpenAPI projection is strict-generator clean.
