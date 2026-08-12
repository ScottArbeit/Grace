[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory, ValueFromRemainingArguments)][ValidateNotNullOrEmpty()][string[]] $ArtifactPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleProjection.psm1') -Force
Test-WduLifecycleProjection -CanonicalPath $CanonicalPath -ArtifactPath $ArtifactPath
