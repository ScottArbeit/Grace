[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Artifact
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleContract.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleRawProjection.psm1') -Force
$compiled = Read-WduLifecycleContract -Path $CanonicalPath
$projection = New-WduLifecycleRawProjection -Compiled $compiled -Artifact $Artifact
$bytes = $projection.Utf8Json.ToArray()
[Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length)
