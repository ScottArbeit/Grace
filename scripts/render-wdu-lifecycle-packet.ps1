[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PacketDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecyclePacket.psm1') -Force
Export-WduLifecyclePacket -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory -OutputDirectory $OutputDirectory
