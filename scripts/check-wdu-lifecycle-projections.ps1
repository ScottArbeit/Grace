[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PacketDirectory
)

& (Join-Path $PSScriptRoot 'test-wdu-lifecycle-packet.ps1') -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory
