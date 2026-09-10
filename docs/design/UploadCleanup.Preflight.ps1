#Requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Extract the exact captured bytes only into a new, explicitly selected disposable directory.
$targetRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $targetRoot) { throw 'Choose a new extraction directory.' }
$bundle = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UploadCleanup.Preflight.json') -Raw | ConvertFrom-Json
foreach ($entry in $bundle.Files) {
    $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $entry.Path))
    if (-not $target.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Capture path escapes extraction directory.' }
    $bytes = [Convert]::FromBase64String($entry.Base64)
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($actual -ne $entry.SHA256) { throw "Capture hash mismatch: $($entry.Path)" }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    [IO.File]::WriteAllBytes($target, $bytes)
    Write-Output "$($entry.Path): $actual"
}
Write-Output "Verified $($bundle.Files.Count) exact captures in $targetRoot"
