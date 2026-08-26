[CmdletBinding()]
param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'modules/WduLifecycleContract.psm1') -Force
try { Read-WduLifecycleContract -Path $Path }
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
