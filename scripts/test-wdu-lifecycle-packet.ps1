[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'tests/WduLifecyclePacket.Tests.ps1')
if ($?) { exit 0 }
exit 1
