[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [string] $Artifact
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Artifact)) {
    [Console]::Error.WriteLine('WDU raw lifecycle export: artifact is required.')
    exit 1
}

try {
    Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleRawProjection.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleContract.psm1') -Force
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $projection = New-WduLifecycleRawProjection -Compiled $compiled -Artifact $Artifact
    $bytes = $projection.Utf8Json.ToArray()
    [Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length)
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
