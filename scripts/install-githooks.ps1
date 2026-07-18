[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$hookDirRelative = (& git -C $repoRoot rev-parse --git-path hooks).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($hookDirRelative)) {
    throw "Unable to locate Git hooks. Run from inside a Git clone."
}

$hookDir =
    if ([System.IO.Path]::IsPathRooted($hookDirRelative)) {
        $hookDirRelative
    } else {
        Join-Path $repoRoot $hookDirRelative
    }

New-Item -ItemType Directory -Force -Path $hookDir | Out-Null
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
    $hook = @'
#!/bin/sh
# Grace Validate Hook (installed by scripts/install-githooks.ps1)
# Runs a lightweight staged-diff check after any existing hook logic.
# Focused tests and formatting remain explicit developer responsibilities.

HOOK_DIR=$(cd "${0%/*}" && pwd)
BACKUP="$HOOK_DIR/pre-commit.grace.bak"

if [ -x "$BACKUP" ]; then
  "$BACKUP" "$@" || exit $?
fi

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || {
  echo "Grace hook: unable to locate repository root." >&2
  exit 1
}

git -C "$REPO_ROOT" diff --cached --check || {
  echo "Grace hook: staged whitespace or conflict-marker errors must be fixed before committing." >&2
  exit 1
}
'@

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
Write-Host "Installed Grace pre-commit hook (staged-diff check only; no build, tests, or network operations)."
Write-Host "Focused tests and formatting remain explicit developer responsibilities."
Write-Host "Run with -Uninstall to restore the previous hook."
