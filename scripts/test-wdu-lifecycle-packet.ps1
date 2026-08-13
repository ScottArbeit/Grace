[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PacketDirectory,
    [ValidateNotNullOrEmpty()][string] $RenderDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleProjection.psm1') -Force
if ($PSBoundParameters.ContainsKey('RenderDirectory')) {
    Export-WduLifecycleProjection -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory -OutputDirectory $RenderDirectory
}
else { Test-WduLifecycleProjection -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory }
