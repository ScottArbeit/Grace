[CmdletBinding()]
param(
    [string] $CanonicalPath = (Join-Path $PSScriptRoot '../docs/Working Directory Update.md'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PacketDirectory,
    [ValidateNotNullOrEmpty()][string] $RenderDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleProjection.psm1') -Force

$packet = [IO.Path]::GetFullPath($PacketDirectory)
if (-not (Test-Path -LiteralPath $packet -PathType Container)) { throw "WDU lifecycle packet '$packet' does not exist" }
$paths = @(Get-ChildItem -LiteralPath $packet -File -Filter '*.md' | Sort-Object Name | Select-Object -ExpandProperty FullName)
if ($PSBoundParameters.ContainsKey('RenderDirectory')) {
    Export-WduLifecycleProjection -CanonicalPath $CanonicalPath -ArtifactPath $paths -OutputDirectory $RenderDirectory
}
else {
    Test-WduLifecycleProjection -CanonicalPath $CanonicalPath -ArtifactPath $paths
}
