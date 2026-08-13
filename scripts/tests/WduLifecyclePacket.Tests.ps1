[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $root 'scripts/modules/WduLifecycleProjection.psm1') -Force
Import-Module (Join-Path $root 'scripts/modules/WduLifecycleContract.psm1') -Force

function Assert-True { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw $Message } }
function Assert-Fails { param([scriptblock] $Action, [string] $Text) try { & $Action | Out-Null } catch { if ($_.Exception.Message.Contains($Text, [StringComparison]::Ordinal)) { return }; throw }; throw "Expected '$Text'" }
function Get-MarkerForTest { param([string] $Artifact, [string] $Kind) "<!-- grace:wdu-lifecycle-projection:$Artifact`:$Kind -->" }

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-936-' + [guid]::NewGuid().ToString('N'))
$module = $null
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $canonical = Join-Path $root 'docs/Working Directory Update.md'
    $compiled = Read-WduLifecycleContract -Path $canonical
    $seed = Join-Path $testRoot 'seed'
    $fence = '```'
    [IO.Directory]::CreateDirectory($seed) | Out-Null
    foreach ($artifact in $compiled.Artifacts) {
        $line = if (($artifact.Id.GetHashCode() % 2) -eq 0) { "`r`n" } else { "`n" }
        $bom = if ($artifact.Id -eq 'adr-0011') { [byte[]](239,187,191) } else { [byte[]]::new(0) }
        $body = "prefix-$($artifact.Id)-é${line}$(Get-MarkerForTest $artifact.Id 'start')${line}${fence}json${line}{}${line}${fence}${line}$(Get-MarkerForTest $artifact.Id 'end')${line}suffix-$($artifact.Id)-é"
        $content = [Text.UTF8Encoding]::new($false, $true).GetBytes($body)
        [IO.File]::WriteAllBytes((Join-Path $seed "$($artifact.Id).md"), [byte[]]($bom + $content))
    }
    $before = @(Get-ChildItem $seed -File | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash })
    $rendered = Join-Path $testRoot 'rendered'
    $result = @(Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $rendered)
    Assert-True ($result.Count -eq 15) 'render writes every compiler artifact'
    Assert-True ((@((Get-ChildItem $seed -File | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash })) -join '|') -ceq ($before -join '|')) 'render preserves every input byte'
    Assert-True (@(Test-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $rendered).Count -eq 15) 'rendered packet checks exact'
    Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $rendered } 'render destination already exists'
    $bad = Join-Path $testRoot 'bad'
    Copy-Item -LiteralPath $seed -Destination $bad -Recurse
    $target = Join-Path $bad 'adr-0011.md'
    [IO.File]::WriteAllText($target, ([IO.File]::ReadAllText($target).Replace(':adr-0011:start', ':ADR-0011:start')), [Text.UTF8Encoding]::new($false))
    Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $bad -OutputDirectory (Join-Path $testRoot 'bad-output') } 'unknown artifact marker'
    $module = Get-Module WduLifecycleProjection
    & $module { $script:FailureAfterStagedWritesForTest = 2 }
    Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory (Join-Path $testRoot 'failed') } 'test-only deterministic failure'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'failed'))) 'failed render publishes no partial destination'
    Assert-True (@(Get-ChildItem $testRoot -Directory -Filter '*.staging-*').Count -eq 0) 'failed render leaves no staging residue'
    Write-Host 'Result: 1 passed; 0 failed'
}
finally { if ($null -ne $module) { & $module { $script:FailureAfterStagedWritesForTest = $null } }; if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force } }
