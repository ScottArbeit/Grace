# Grace.Server Local Development with .NET Aspire

This guide is the executable onboarding path for local Grace development.
Use `scripts/start-debuglocal.ps1` as the canonical first-run entrypoint.

## Prerequisites

1. **.NET 10 SDK**: install the version pinned in `global.json` (or compatible roll-forward).
2. **PowerShell 7+**: required because onboarding scripts are `pwsh` scripts.
3. **Docker Desktop**: required for local emulators and containers.
4. **Resources**: reserve at least 8 GB RAM and 10 GB free disk.

## Canonical Onboarding Flow

Run this from the repository root.

PowerShell:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/start-debuglocal.ps1
```

bash / zsh:

```bash
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/start-debuglocal.ps1
```

When startup succeeds, `start-debuglocal.ps1` creates a local PAT and sets `GRACE_SERVER_URI`
and `GRACE_TOKEN` for the current shell.

Compatibility note:

- `pwsh ./scripts/dev-local.ps1` still works, but it is now only a wrapper that forwards to
  `start-debuglocal.ps1`.

## What The Script Does

`start-debuglocal.ps1` performs a deterministic startup workflow:

1. Resolves bootstrap user identity in this order:
   `-BootstrapUserId` -> shell env `grace__authz__bootstrap__system_admin_users` -> launch profile
   value -> `-BootstrapUserIdFallback` (`test-admin` by default).
2. Ensures `GRACE_TESTING=1` when not already present in your shell.
3. Detects stale DebugLocal AppHost processes and attempts cleanup before startup.
4. Starts `Grace.Aspire.AppHost` with `--launch-profile DebugLocal`.
5. Waits for `GET /healthz` to become healthy.
6. Runs auth readiness probes unless `-SkipAuthProbe` is used:
   `GET /auth/oidc/config`, then `GET /auth/me` with `x-grace-user-id`.
7. Creates a PAT with bounded retries and deterministic backoff.
8. Prints shell-ready environment commands and log paths.

## Common Script Options

Use these only when you need to diagnose or customize startup.

PowerShell:

```powershell
# Bypass auth probe preflight (advanced diagnostics only)
pwsh ./scripts/start-debuglocal.ps1 -SkipAuthProbe

# Start runtime only (skip PAT bootstrap)
pwsh ./scripts/start-debuglocal.ps1 -NoTokenBootstrap

# Override bootstrap user and retry behavior
pwsh ./scripts/start-debuglocal.ps1 `
  -BootstrapUserId "test-admin" `
  -TokenBootstrapMaxAttempts 5 `
  -TokenBootstrapInitialBackoffSeconds 2
```

bash / zsh:

```bash
# Bypass auth probe preflight (advanced diagnostics only)
pwsh ./scripts/start-debuglocal.ps1 --SkipAuthProbe

# Start runtime only (skip PAT bootstrap)
pwsh ./scripts/start-debuglocal.ps1 --NoTokenBootstrap

# Override bootstrap user and retry behavior
pwsh ./scripts/start-debuglocal.ps1 \
  --BootstrapUserId "test-admin" \
  --TokenBootstrapMaxAttempts 5 \
  --TokenBootstrapInitialBackoffSeconds 2
```

## Manual Aspire Start (Advanced)

Use this only when you explicitly want to bypass onboarding automation.
You will need to handle auth and token setup manually.

PowerShell:

```powershell
dotnet run --project ./src/Grace.Aspire.AppHost/Grace.Aspire.AppHost.csproj --launch-profile DebugLocal
```

bash / zsh:

```bash
dotnet run --project ./src/Grace.Aspire.AppHost/Grace.Aspire.AppHost.csproj --launch-profile DebugLocal
```

## Verify Runtime Health

The Aspire dashboard is available at <http://localhost:18888>.

Health endpoint checks:

PowerShell:

```powershell
(Invoke-WebRequest -Uri "http://localhost:5000/healthz").Content
```

bash / zsh:

```bash
curl http://localhost:5000/healthz
```

Expected output contains: `Grace server seems healthy!`.

## Diagnostics And Smoke Artifacts

`start-debuglocal.ps1` writes onboarding artifacts under `.grace/logs`:

- `start-debuglocal-<run-id>.stdout.log`
- `start-debuglocal-<run-id>.stderr.log`
- `start-debuglocal-<run-id>.failure.json` (failure summary)
- `start-debuglocal-<run-id>.runtime-metadata.json` (runtime metadata snapshot)

Failure-path cleanup behavior:

- stale DebugLocal AppHost processes are detected before launch and cleanup is attempted;
- process cleanup is also attempted in `finally` when a startup failure occurs;
- cleanup outcomes are recorded in failure diagnostics.

## Smoke Validation Checklist

Use this checklist when reviewing onboarding reliability updates:

1. Run onboarding from a clean shell and confirm startup completes.

PowerShell:

```powershell
pwsh ./scripts/start-debuglocal.ps1
```

bash / zsh:

```bash
pwsh ./scripts/start-debuglocal.ps1
```

1. Confirm script output includes:
   - resolved bootstrap user and source,
   - auth probe pass or fail details,
   - token bootstrap attempt summary,
   - cleanup and diagnostics paths on failure.
2. Validate docs and repo fast checks:

PowerShell:

```powershell
npx --yes markdownlint-cli2 src/docs/**/*.md
pwsh ./scripts/validate.ps1 -Fast
```

bash / zsh:

```bash
npx --yes markdownlint-cli2 src/docs/**/*.md
pwsh ./scripts/validate.ps1 -Fast
```

## Troubleshooting

- **Startup timeout**: increase `-StartupTimeoutSeconds` and inspect `.grace/logs/*.stderr.log`.
- **Auth probe fails with 401/403**: ensure the bootstrap user resolves correctly and `GRACE_TESTING=1` is set.
- **Token bootstrap retry exhaustion**: inspect `*.failure.json` for classification and retry diagnostics.
- **Ports already in use**: stop conflicting processes, then rerun `start-debuglocal.ps1`.
