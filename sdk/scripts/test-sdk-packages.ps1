[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $root = git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
        throw 'Unable to resolve repository root with git rev-parse.'
    }

    return $root.Trim()
}

function Invoke-Step {
    param(
        [string] $Name,
        [string] $WorkingDirectory,
        [scriptblock] $Command
    )

    Write-Host "==> $Name"
    Push-Location $WorkingDirectory
    try {
        & $Command
        if ($LASTEXITCODE -ne 0) {
            throw "$Name failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$repoRoot = Get-RepoRoot

Invoke-Step 'TypeScript facade import smoke' (Join-Path $repoRoot 'sdk/typescript/grace') {
    npm ci --ignore-scripts
    npm run smoke
}

Invoke-Step 'Python facade import smoke' (Join-Path $repoRoot 'sdk/python/grace-sdk') {
    python ./smoke/import_facade.py
}

Invoke-Step '.NET facade build smoke' (Join-Path $repoRoot 'sdk/dotnet/Grace.Sdk.Harness') {
    dotnet build --nologo
}

Invoke-Step 'Rust facade test smoke' (Join-Path $repoRoot 'sdk/rust/grace-sdk') {
    cargo test
}

Write-Host 'SDK package import/export smoke tests passed.'
