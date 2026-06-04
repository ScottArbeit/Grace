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
    }
    finally {
        Pop-Location
    }
}

function Invoke-NativeCommand {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        $commandText = @($FilePath) + $ArgumentList -join ' '
        throw "Native command failed with exit code $LASTEXITCODE`: $commandText"
    }
}

$repoRoot = Get-RepoRoot
$npmExecutable = if ($IsWindows) { 'npm.cmd' } else { 'npm' }

Invoke-Step 'TypeScript facade import smoke' (Join-Path $repoRoot 'sdk/typescript/grace') {
    Invoke-NativeCommand -FilePath $npmExecutable -ArgumentList @('ci', '--ignore-scripts')
    Invoke-NativeCommand -FilePath $npmExecutable -ArgumentList @('run', 'smoke')
}

Invoke-Step 'Python facade import smoke' (Join-Path $repoRoot 'sdk/python/grace-sdk') {
    Invoke-NativeCommand -FilePath 'python' -ArgumentList @('./smoke/import_facade.py')
}

Invoke-Step '.NET facade build smoke' (Join-Path $repoRoot 'sdk/dotnet/Grace.Sdk.Harness') {
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('build', '--nologo')
}

Invoke-Step 'Rust facade test smoke' (Join-Path $repoRoot 'sdk/rust/grace-sdk') {
    Invoke-NativeCommand -FilePath 'cargo' -ArgumentList @('test')
}

Write-Host 'SDK package import/export smoke tests passed.'
