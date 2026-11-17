#Requires -Version 7.6
<#
.SYNOPSIS
Reproduces the disposable Library catalog-adoption tracer against pinned Grace source.
.DESCRIPTION
Archives tracked source into a new artifact directory, appends the experiment fixture and applies two prototype guards.
It creates no Git branch/worktree and edits no production checkout. Hosted resources are test-owned.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = 'C:/Source/Grace',
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$adoptionRevision = '9b18c97ab342d319d3bdeaedc5f1d64571913cf5'
$adoptionOutput = [IO.Path]::GetFullPath($OutputRoot)
if (-not $IsWindows) { throw 'Windows is required.' }
if (Test-Path -LiteralPath $adoptionOutput) { throw 'Use a new output directory; prior evidence is preserved.' }
New-Item -ItemType Directory -Path $adoptionOutput | Out-Null
$adoptionArchive = Join-Path $adoptionOutput 'source.zip'
git -C $RepositoryRoot archive --format=zip --output $adoptionArchive $adoptionRevision
if ($LASTEXITCODE -ne 0) { throw 'Pinned source archive failed.' }
$adoptionSource = Join-Path $adoptionOutput 'source'
Expand-Archive -LiteralPath $adoptionArchive -DestinationPath $adoptionSource
$adoptionFixture = Join-Path $adoptionSource 'src/Grace.Server.Tests/LibrarySynchronization.Windows.Server.Tests.fs'
$adoptionOriginal = git -C $RepositoryRoot show "${adoptionRevision}:src/Grace.Server.Tests/LibrarySynchronization.Windows.Server.Tests.fs"
if ($LASTEXITCODE -ne 0) { throw 'Pinned fixture read failed.' }
$adoptionFragment = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'AdoptionExperiment.fs.fragment')
[IO.File]::WriteAllText($adoptionFixture, ($adoptionOriginal -join "`n") + "`n" + $adoptionFragment, [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Apply-Prototype.ps1') -SourceRoot $adoptionSource
$adoptionOldResults = $env:GRACE_ADOPTION_RESULTS
Push-Location -LiteralPath $adoptionSource
try {
    dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release -v quiet *> (Join-Path $adoptionOutput 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Experiment build failed; inspect build.log.' }
    $env:GRACE_ADOPTION_RESULTS = Join-Path $adoptionOutput 'results'
    dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'TestCategory=CatalogAdoptionReadiness' --logger 'trx;LogFileName=adoption.trx' --results-directory (Join-Path $adoptionOutput 'results') *> (Join-Path $adoptionOutput 'test.log')
    $adoptionExit = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $adoptionOutput 'test.log') -Tail 40
    if ($adoptionExit -ne 0) { throw 'Experiment failed; preserve and inspect its evidence.' }
}
finally {
    $env:GRACE_ADOPTION_RESULTS = $adoptionOldResults
    Pop-Location
}
