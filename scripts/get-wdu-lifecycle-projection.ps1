[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Artifact
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleProjection.psm1') -Force
Write-Output (New-WduLifecycleProjection -CanonicalPath $CanonicalPath -Artifact $Artifact)
