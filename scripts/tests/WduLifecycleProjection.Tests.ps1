[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$modulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleProjection.psm1'
$contractModulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleContract.psm1'
$canonicalPath = Join-Path $repositoryRoot 'docs/Working Directory Update.md'

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Fails {
    param([scriptblock] $Body, [string] $Contains)
    try { & $Body | Out-Null }
    catch {
        Assert-True $_.Exception.Message.Contains($Contains, [StringComparison]::Ordinal) "failure should contain '$Contains': $($_.Exception.Message)"
        return
    }
    throw "Expected failure containing '$Contains'"
}

function Invoke-Case {
    param([string] $Name, [scriptblock] $Body)
    try { & $Body; $script:Passed++; Write-Host "PASS $Name" }
    catch { $script:Failed++; Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red }
}

function Write-TestText {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-TestPacket {
    param([string] $Name, [switch] $CrLf)
    $directory = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($artifact in $script:Compiled.Artifacts) {
        $path = Join-Path $directory ($artifact.Id + '.md')
        $outside = "# $($artifact.Id)`n`nHistorical prose, dates, #920, and contradictory counts are not interpreted.`n`n"
        $text = $outside + $script:Projections[$artifact.Id] + "`n`n$outside"
        if ($CrLf) { $text = $text.Replace("`n", "`r`n") }
        Write-TestText $path $text
        $paths.Add($path)
    }
    return [pscustomobject]@{ Directory = $directory; Paths = @($paths) }
}

function Get-Hashes {
    param([string[]] $Path)
    return @($Path | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
}

function Replace-Once {
    param([string] $Text, [string] $Old, [string] $New)
    $index = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Test anchor not found: $Old" }
    return $Text.Remove($index, $Old.Length).Insert($index, $New)
}

Import-Module $modulePath -Force
Import-Module $contractModulePath -Force
$script:Compiled = Read-WduLifecycleContract -Path $canonicalPath
$script:Projections = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($artifact in $script:Compiled.Artifacts) {
    $script:Projections.Add($artifact.Id, (New-WduLifecycleProjection -CanonicalPath $canonicalPath -Artifact $artifact.Id))
}
$script:Passed = 0
$script:Failed = 0
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-lifecycle-projection-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null

try {
    Invoke-Case 'derives deterministic complete metadata from only the compiler result' {
        $artifact = $script:Compiled.Artifacts[0]
        $first = New-WduLifecycleProjection -CanonicalPath $canonicalPath -Artifact $artifact.Id
        $second = New-WduLifecycleProjection -CanonicalPath $canonicalPath -Artifact $artifact.Id
        Assert-True ($first -ceq $second) 'repeated generation is byte-identical'
        Assert-True $first.Contains("projection:$($artifact.Id):start", [StringComparison]::Ordinal) 'marker is artifact-specific'
        Assert-True $first.Contains(('"artifactCount": ' + $script:Compiled.Counts.artifactCount), [StringComparison]::Ordinal) 'artifact count comes from compiler'
        foreach ($requirement in $script:Compiled.Requirements) {
            Assert-True $first.Contains(('"id": "' + $requirement.Id + '"'), [StringComparison]::Ordinal) "projection includes $($requirement.Id)"
            Assert-True $first.Contains(('"owner": "' + $requirement.Owner + '"'), [StringComparison]::Ordinal) "projection includes $($requirement.Owner)"
        }
    }

    Invoke-Case 'checks all compiler artifacts in canonical order while ignoring prose outside blocks' {
        $packet = Get-TestPacket 'complete' -CrLf
        $result = @(Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath @($packet.Paths | Sort-Object -Descending))
        Assert-True ($result.Count -eq $script:Compiled.Artifacts.Count) 'returns every compiler artifact'
        Assert-True (($result.Artifact -join '|') -ceq (($script:Compiled.Artifacts.Id) -join '|')) 'returns compiler order'
    }

    Invoke-Case 'rejects stale generated block content even when equivalent prose remains outside it' {
        $packet = Get-TestPacket 'stale-block'
        $path = $packet.Paths[0]
        $text = [IO.File]::ReadAllText($path)
        $old = '"canonicalContentDigest": "' + $script:Compiled.Digest + '"'
        Write-TestText $path (Replace-Once $text $old '"canonicalContentDigest": "0000000000000000000000000000000000000000000000000000000000000000"')
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths } 'does not equal its exact generated projection'
    }

    Invoke-Case 'rejects malformed, duplicate, reversed, nested, and unknown markers' {
        $mutations = @(
            @{ Old = ':start -->'; New = ':started -->'; Error = 'malformed lifecycle projection marker' },
            @{ Old = ':start -->'; New = ":start -->`n<!-- grace:wdu-lifecycle-projection:$($script:Compiled.Artifacts[0].Id):start -->"; Error = 'malformed lifecycle projection marker' },
            @{ Old = ':start -->'; New = ':end -->'; Error = 'reversed end marker' },
            @{ Old = ':start -->'; New = ':start -->`n<!-- grace:wdu-lifecycle-projection:unknown:start -->`n<!-- grace:wdu-lifecycle-projection:unknown:end -->'; Error = 'nested lifecycle projection markers' }
        )
        $errors = @()
        foreach ($mutation in $mutations) {
            $packet = Get-TestPacket ([guid]::NewGuid().ToString('N'))
            $path = $packet.Paths[0]
            Write-TestText $path (Replace-Once ([IO.File]::ReadAllText($path)) $mutation.Old $mutation.New)
            try { Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths | Out-Null }
            catch { $errors += $_.Exception.Message; continue }
            throw 'marker mutation unexpectedly passed'
        }
        Assert-True ($errors.Count -eq $mutations.Count) 'all marker mutations fail'
    }

    Invoke-Case 'rejects missing, duplicate, and extra packet artifacts without scanning prose' {
        $packet = Get-TestPacket 'membership'
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths[0..($packet.Paths.Count - 2)] } 'packet is missing required artifact'
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath @($packet.Paths + $packet.Paths[0]) } 'packet contains duplicate artifact'
        $extra = Join-Path $packet.Directory 'extra.md'
        Write-TestText $extra (New-WduLifecycleProjection -CanonicalPath $canonicalPath -Artifact $script:Compiled.Artifacts[0].Id)
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath @($packet.Paths + $extra) } 'artifact path name must equal'
    }

    Invoke-Case 'renders a complete packet twice without changing inputs and rejects unsafe destinations before writes' {
        $packet = Get-TestPacket 'render-input'
        $stalePath = $packet.Paths[0]
        Write-TestText $stalePath (Replace-Once ([IO.File]::ReadAllText($stalePath)) '"assignmentDigest": "' + $script:Compiled.AssignmentDigest + '"' '"assignmentDigest": "0000000000000000000000000000000000000000000000000000000000000000"')
        $before = Get-Hashes $packet.Paths
        $first = Join-Path $script:TestRoot 'render-one'
        $second = Join-Path $script:TestRoot 'render-two'
        $firstPaths = @(Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $first)
        $secondPaths = @(Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $second)
        Assert-True (($firstPaths.Count -eq $script:Compiled.Artifacts.Count) -and ($secondPaths.Count -eq $script:Compiled.Artifacts.Count)) 'both complete renders contain compiler artifacts'
        Assert-True ((Get-Hashes $packet.Paths) -join '|' -ceq ($before -join '|')) 'render leaves inputs byte-identical'
        Assert-True ((Get-Hashes $firstPaths) -join '|' -ceq ((Get-Hashes $secondPaths) -join '|')) 'complete renders are deterministic'
        Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $firstPaths | Out-Null
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $first } 'render destination already exists'
    }

    Invoke-Case 'rejects a partial packet before creating an output directory' {
        $packet = Get-TestPacket 'render-preflight'
        $destination = Join-Path $script:TestRoot 'must-not-exist'
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths[0..($packet.Paths.Count - 2)] -OutputDirectory $destination } 'packet is missing required artifact'
        Assert-True (-not (Test-Path -LiteralPath $destination)) 'failed preflight creates no destination'
    }
}
finally {
    if (Test-Path -LiteralPath $script:TestRoot) { Remove-Item -LiteralPath $script:TestRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
