[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PacketDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecyclePacket.psm1') -Force
Test-WduLifecyclePacket -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory
