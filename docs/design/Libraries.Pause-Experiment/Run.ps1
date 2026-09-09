#Requires -Version 7.6
<#
.SYNOPSIS
Runs the disposable Windows Library pause/resume experiment against its pinned Grace source.
.DESCRIPTION
Copies only the experiment program/project into a new scratch directory. Builds may create ignored
outputs in SourceRoot. The runner does not edit production source or create/remove Git worktrees.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot,
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ('Grace-library-pause-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This experiment requires Windows.' }
$pauseBase = '66aea34838a65a9e4b220463f0d8366836ee328d'
$pauseSource = (Resolve-Path -LiteralPath $SourceRoot).Path
$pauseOutput = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $pauseOutput) { throw 'Use a new output directory; preserved runs are immutable.' }
$pauseHead = git -C $pauseSource rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'SourceRoot is not a Git checkout.' }
git -C $pauseSource diff --quiet $pauseBase -- . ':(exclude)docs/**'
if ($LASTEXITCODE -ne 0) { throw 'Non-documentation source differs from the pinned experiment base.' }
$pauseDirtySource = @(git -C $pauseSource status --porcelain --untracked-files=normal -- src)
if ($LASTEXITCODE -ne 0 -or $pauseDirtySource.Count -ne 0) { throw 'SourceRoot has uncommitted source changes.' }
$pauseStart = [DateTime]::UtcNow.ToString('o')
$pauseProject = Join-Path $pauseOutput 'project'
New-Item -ItemType Directory -Path $pauseProject | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.fs'), (Join-Path $PSScriptRoot 'PauseExperiment.fsproj') -Destination $pauseProject
$pauseProjectFile = Join-Path $pauseProject 'PauseExperiment.fsproj'
$pauseRun = Join-Path $pauseOutput 'run'

Push-Location -LiteralPath $pauseSource
try {
    dotnet build $pauseProjectFile -c Release "-p:GraceSourceRoot=$pauseSource" -v quiet *> (Join-Path $pauseOutput 'build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $pauseOutput 'build.log') -Tail 35; throw 'Experiment build failed.' }
    dotnet (Join-Path $pauseProject 'bin/Release/net10.0/Grace.CLI.Tests.dll') $pauseRun *> (Join-Path $pauseOutput 'run.log')
    $pauseExit = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $pauseOutput 'run.log')
    $pauseResultFile = Join-Path $pauseRun 'results.json'
    if (-not (Test-Path -LiteralPath $pauseResultFile)) { throw 'Experiment did not emit results.' }
    $pauseResult = Get-Content -LiteralPath $pauseResultFile -Raw | ConvertFrom-Json -AsHashtable
    $pauseResult['SourceRevision'] = $pauseHead
    $pauseResult['SourceBase'] = $pauseBase
    $pauseResult['ProgramSha256'] = (Get-FileHash -LiteralPath (Join-Path $pauseProject 'Program.fs') -Algorithm SHA256).Hash
    $pauseResult['RunnerSha256'] = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $pauseResult['StartedUtc'] = $pauseStart
    $pauseResult['CompletedUtc'] = [DateTime]::UtcNow.ToString('o')
    $pauseResult['ArtifactRoot'] = $pauseOutput
    $pauseResult | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $pauseResultFile -Encoding utf8
    if ($pauseExit -ne 0 -or $pauseResult.Passed -ne $pauseResult.Total) { throw 'Experiment assertions failed.' }
    Write-Output ("Passed {0}/{1}. Results: {2}" -f $pauseResult.Passed, $pauseResult.Total, $pauseResultFile)
}
finally {
    Pop-Location
}
