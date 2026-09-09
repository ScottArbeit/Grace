#Requires -Version 7.6
<#
.SYNOPSIS
Replays the preserved object witness against its exact pre-redesign Grace API in a disposable runner directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaselineRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$baselinePath = (Resolve-Path -LiteralPath $BaselineRoot).Path
$expectedHead = '1e5e0e37fab5fafbf1ac55d8607c10addb8e9dca'
$actualHead = & git -C $baselinePath rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualHead.Trim() -ne $expectedHead) {
    throw "Provide a preserved baseline worktree at $expectedHead."
}
$sourceChanges = & git -C $baselinePath status --porcelain -- src global.json Directory.Build.props Directory.Packages.props
if ($LASTEXITCODE -ne 0 -or $sourceChanges) {
    throw 'The baseline source and build inputs must be clean.'
}

$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('grace-object-witness-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.fs'), (Join-Path $PSScriptRoot 'ObjectCapture.fsproj') -Destination $runDirectory
& dotnet run --project (Join-Path $runDirectory 'ObjectCapture.fsproj') --configuration Release "-p:GraceSourceRoot=$baselinePath"
if ($LASTEXITCODE -ne 0) { throw "Object capture experiment failed with exit code $LASTEXITCODE. Results: $runDirectory" }
Write-Output "Object experiment results: $(Join-Path $runDirectory 'results.json')"
