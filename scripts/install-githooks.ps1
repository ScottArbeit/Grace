[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$gitDir = Join-Path $repoRoot ".git"

if (-not (Test-Path $gitDir)) {
    throw "No .git directory found at $gitDir. Run from inside a Git clone."
}

$hookDir = Join-Path $gitDir "hooks"
$hookPath = Join-Path $hookDir "pre-commit"
$backupPath = Join-Path $hookDir "pre-commit.grace.bak"
$marker = "Grace Validate Hook"

function Get-FileContent([string]$Path) {
    if (-not (Test-Path $Path)) {
        return ""
    }

    return (Get-Content -Path $Path -Raw)
}

function Write-Hook([string]$Path) {
    $hook = @"
#!/usr/bin/env sh
# Grace Validate Hook (installed by scripts/install-githooks.ps1)
# Runs validate -Fast after any existing hook logic.

HOOK_DIR=$(cd "$(dirname "$0")" && pwd)
BACKUP="$HOOK_DIR/pre-commit.grace.bak"

if [ -x "$BACKUP" ]; then
  "$BACKUP" "$@" || exit $?
fi

if command -v pwsh >/dev/null 2>&1; then
  REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null)
  if [ -n "$REPO_ROOT" ]; then
    pwsh -NoProfile -ExecutionPolicy Bypass -File "$REPO_ROOT/scripts/validate.ps1" -Fast || exit $?
  else
    echo "Grace hook: unable to locate repo root; skipping validate." >&2
  fi
else
  echo "Grace hook: pwsh not found; skipping validate." >&2
fi
"@

    Set-Content -Path $Path -Value $hook -Encoding ASCII
}

if ($Uninstall) {
    $current = Get-FileContent $hookPath

    if ($current -match $marker) {
        if (Test-Path $backupPath) {
            Copy-Item -Path $backupPath -Destination $hookPath -Force
            Remove-Item -Path $backupPath -Force
            Write-Host "Restored original pre-commit hook."
        } else {
            Remove-Item -Path $hookPath -Force
            Write-Host "Removed Grace pre-commit hook."
        }
    } else {
        Write-Host "Grace pre-commit hook not installed. No changes made."
    }

    return
}

$existing = Get-FileContent $hookPath
if ($existing -match $marker) {
    Write-Host "Grace pre-commit hook already installed."
    return
}

if (Test-Path $hookPath) {
    if ((Test-Path $backupPath) -and -not $Force) {
        throw "Backup already exists at $backupPath. Use -Force to overwrite."
    }

    Copy-Item -Path $hookPath -Destination $backupPath -Force
}

Write-Hook $hookPath
Write-Host "Installed Grace pre-commit hook (validate -Fast)."
Write-Host "Run with -Uninstall to restore the previous hook."
