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
    param([string] $Path, [string] $Text, [switch] $Utf8Bom)
    $encoding = [Text.UTF8Encoding]::new($Utf8Bom, $true)
    $content = $encoding.GetBytes($Text)
    $preamble = $encoding.GetPreamble()
    $bytes = [byte[]]::new($preamble.Length + $content.Length)
    [Array]::Copy($preamble, 0, $bytes, 0, $preamble.Length)
    [Array]::Copy($content, 0, $bytes, $preamble.Length, $content.Length)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Get-TestPacket {
    param([string] $Name, [switch] $CrLf, [switch] $AlternatingByteBoundaries)
    $directory = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $paths = [Collections.Generic.List[string]]::new()
    $byteBoundaries = [Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($artifact in $script:Compiled.Artifacts) {
        $path = Join-Path $directory ($artifact.Id + '.md')
        $lineEnding = if ($CrLf) { "`r`n" } elseif ($AlternatingByteBoundaries -and (($index % 2) -eq 1)) { "`r`n" } else { "`n" }
        $utf8Bom = $AlternatingByteBoundaries -and (($index % 2) -eq 0)
        $outside = "# $($artifact.Id)`n`nHistorical prose, dates, #920, and contradictory counts are not interpreted.`n`n"
        $text = $outside + $script:Projections[$artifact.Id] + "`n`n$outside"
        if ($lineEnding -eq "`r`n") { $text = $text.Replace("`n", "`r`n") }
        Write-TestText $path $text -Utf8Bom:$utf8Bom
        [void]$paths.Add($path)
        [void]$byteBoundaries.Add([pscustomobject]@{ Path = $path; HasUtf8Bom = $utf8Bom; LineEnding = $lineEnding })
        $index++
    }
    return [pscustomobject]@{ Directory = $directory; Paths = @($paths); ByteBoundaries = @($byteBoundaries) }
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

function Set-TestStagedWriteFailure {
    param([int] $After)
    & (Get-Module WduLifecycleProjection) {
        param([int] $Value)
        $script:FailureAfterStagedWritesForTest = $Value
    } $After
}

function Clear-TestStagedWriteFailure {
    & (Get-Module WduLifecycleProjection) {
        $script:FailureAfterStagedWritesForTest = $null
    }
}

function Assert-ByteSequenceEqual {
    param([byte[]] $Actual, [byte[]] $Expected, [string] $Message)
    Assert-True ($Actual.Length -eq $Expected.Length) "$Message length"
    for ($index = 0; $index -lt $Actual.Length; $index++) {
        Assert-True ($Actual[$index] -eq $Expected[$index]) "$Message at byte $index"
    }
}

function Find-ByteSequence {
    param([byte[]] $Bytes, [byte[]] $Needle, [int] $StartAt = 0)
    if ($Needle.Length -eq 0) { throw 'Test byte sequence must not be empty' }
    for ($index = $StartAt; $index -le ($Bytes.Length - $Needle.Length); $index++) {
        $matches = $true
        for ($offset = 0; $offset -lt $Needle.Length; $offset++) {
            if ($Bytes[$index + $offset] -ne $Needle[$offset]) {
                $matches = $false
                break
            }
        }
        if ($matches) { return $index }
    }
    return -1
}

function Get-ByteSlice {
    param([byte[]] $Bytes, [int] $Start, [int] $Length)
    if ($Length -eq 0) { return [byte[]]::new(0) }
    $slice = [byte[]]::new($Length)
    [Array]::Copy($Bytes, $Start, $slice, 0, $Length)
    return $slice
}

function Get-IndependentProjectionSpan {
    param([byte[]] $Bytes, [string] $Artifact, [string] $Subject)
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $startMarker = $encoding.GetBytes("<!-- grace:wdu-lifecycle-projection:$Artifact`:start -->")
    $endMarker = $encoding.GetBytes("<!-- grace:wdu-lifecycle-projection:$Artifact`:end -->")
    $startIndex = Find-ByteSequence $Bytes $startMarker
    $endIndex = Find-ByteSequence $Bytes $endMarker
    Assert-True ($startIndex -ge 0) "$Subject has the independently located start marker"
    Assert-True ($endIndex -gt $startIndex) "$Subject has the independently located end marker after its start"
    Assert-True ((Find-ByteSequence $Bytes $startMarker ($startIndex + 1)) -lt 0) "$Subject has one start marker"
    Assert-True ((Find-ByteSequence $Bytes $endMarker ($endIndex + 1)) -lt 0) "$Subject has one end marker"
    return [pscustomobject]@{
        StartIndex = $startIndex
        EndIndex = $endIndex
        EndLength = $endMarker.Length
    }
}

function Get-IndependentProjectionPayload {
    param([string] $Path, [string] $Artifact)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $span = Get-IndependentProjectionSpan $bytes $Artifact $Path
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $block = $encoding.GetString((Get-ByteSlice $bytes $span.StartIndex (($span.EndIndex + $span.EndLength) - $span.StartIndex)))
    $normalizedBlock = $block.Replace("`r`n", "`n").Replace("`r", "`n")
    $startMarker = "<!-- grace:wdu-lifecycle-projection:$Artifact`:start -->" + "`n" + '```json' + "`n"
    $endMarker = "`n" + '```' + "`n" + "<!-- grace:wdu-lifecycle-projection:$Artifact`:end -->"
    Assert-True $normalizedBlock.StartsWith($startMarker, [StringComparison]::Ordinal) "$Path has the expected JSON fence after its start marker"
    Assert-True $normalizedBlock.EndsWith($endMarker, [StringComparison]::Ordinal) "$Path has the expected JSON fence before its end marker"
    $json = $normalizedBlock.Substring($startMarker.Length, $normalizedBlock.Length - $startMarker.Length - $endMarker.Length)
    return [pscustomobject]@{ Payload = ($json | ConvertFrom-Json -Depth 8); Json = $json }
}

function Assert-PropertyOrder {
    param([object] $Object, [string[]] $Expected, [string] $Message)
    $actual = @($Object.PSObject.Properties.Name)
    Assert-True (($actual -join '|') -ceq ($Expected -join '|')) $Message
}

function Assert-IndependentPayloadMatchesCompiler {
    param([string] $Path, [string] $Artifact)
    $projection = Get-IndependentProjectionPayload $Path $Artifact
    $payload = $projection.Payload
    $compiledArtifact = @($script:Compiled.Artifacts | Where-Object { $_.Id -ceq $Artifact })
    Assert-True ($compiledArtifact.Count -eq 1) "compiler has exactly one artifact '$Artifact'"
    Assert-PropertyOrder $payload @('schema', 'artifact', 'canonicalContentDigest', 'assignmentDigest', 'counts', 'requirements', 'artifactIds', 'assignment') "$Path has the complete ordered payload shape"
    Assert-True ($payload.schema -ceq 'grace.wdu.lifecycle-projection/v2') "$Path has the fixed public schema"
    Assert-True ($payload.artifact -ceq $Artifact) "$Path has its artifact identity"
    Assert-True ($payload.canonicalContentDigest -ceq $script:Compiled.Digest) "$Path has the compiler canonical digest"
    Assert-True ($payload.assignmentDigest -ceq $script:Compiled.AssignmentDigest) "$Path has the compiler assignment digest"
    Assert-PropertyOrder $payload.counts @('rowCount', 'applicabilityKeyCount', 'requirementCount', 'artifactCount') "$Path has the complete ordered count shape"
    foreach ($name in @('rowCount', 'applicabilityKeyCount', 'requirementCount', 'artifactCount')) {
        Assert-True ($payload.counts.$name -eq $script:Compiled.Counts.$name) "$Path has compiler $name"
    }
    Assert-True (@($payload.requirements).Count -eq $script:Compiled.Requirements.Count) "$Path has every compiler requirement"
    for ($index = 0; $index -lt $script:Compiled.Requirements.Count; $index++) {
        Assert-PropertyOrder $payload.requirements[$index] @('id', 'owner') "$Path requirement $index has the complete ordered shape"
        Assert-True ($payload.requirements[$index].id -ceq $script:Compiled.Requirements[$index].Id) "$Path requirement $index has the compiler ID"
        Assert-True ($payload.requirements[$index].owner -ceq $script:Compiled.Requirements[$index].Owner) "$Path requirement $index has the compiler owner"
    }
    Assert-True ((@($payload.artifactIds) -join '|') -ceq (@($script:Compiled.Artifacts.Id) -join '|')) "$Path has ordered compiler artifact IDs"
    Assert-PropertyOrder $payload.assignment @('rowIds') "$Path assignment has the complete ordered shape"
    Assert-True ((@($payload.assignment.rowIds) -join '|') -ceq (@($compiledArtifact[0].RowIds) -join '|')) "$Path has ordered compiler assignment row IDs"
}

function Assert-IndependentOutsideBytesMatch {
    param([string] $SourcePath, [string] $RenderedPath, [string] $Artifact)
    $source = [IO.File]::ReadAllBytes($SourcePath)
    $rendered = [IO.File]::ReadAllBytes($RenderedPath)
    $sourceSpan = Get-IndependentProjectionSpan $source $Artifact $SourcePath
    $renderedSpan = Get-IndependentProjectionSpan $rendered $Artifact $RenderedPath
    Assert-ByteSequenceEqual (Get-ByteSlice $rendered 0 $renderedSpan.StartIndex) (Get-ByteSlice $source 0 $sourceSpan.StartIndex) "$Artifact preserves every prefix byte outside its marker pair"
    $sourceSuffixStart = $sourceSpan.EndIndex + $sourceSpan.EndLength
    $renderedSuffixStart = $renderedSpan.EndIndex + $renderedSpan.EndLength
    Assert-ByteSequenceEqual (Get-ByteSlice $rendered $renderedSuffixStart ($rendered.Length - $renderedSuffixStart)) (Get-ByteSlice $source $sourceSuffixStart ($source.Length - $sourceSuffixStart)) "$Artifact preserves every suffix byte outside its marker pair"
}

function Set-TestPayloadMutation {
    param([string] $Path, [string] $Artifact, [scriptblock] $Mutation)
    $projection = Get-IndependentProjectionPayload $Path $Artifact
    & $Mutation $projection.Payload
    $replacement = $projection.Payload | ConvertTo-Json -Depth 8
    Write-TestText $Path (Replace-Once ([IO.File]::ReadAllText($Path)) $projection.Json $replacement)
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
            @{ Old = ':start -->'; New = ":start -->`n<!-- grace:wdu-lifecycle-projection:$($script:Compiled.Artifacts[0].Id):start -->"; Error = 'nested lifecycle projection markers' },
            @{ Old = ':start -->'; New = ':end -->'; Error = 'reversed end marker' },
            @{ Old = ':start -->'; New = ':start -->`n<!-- grace:wdu-lifecycle-projection:unknown:start -->`n<!-- grace:wdu-lifecycle-projection:unknown:end -->'; Error = "unknown artifact marker 'unknown'" }
        )
        $errors = @()
        foreach ($mutation in $mutations) {
            $packet = Get-TestPacket ([guid]::NewGuid().ToString('N'))
            $path = $packet.Paths[0]
            Write-TestText $path (Replace-Once ([IO.File]::ReadAllText($path)) $mutation.Old $mutation.New)
            try { Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths | Out-Null }
            catch {
                Assert-True $_.Exception.Message.Contains($mutation.Error, [StringComparison]::Ordinal) "marker mutation reports '$($mutation.Error)': $($_.Exception.Message)"
                $errors += $_.Exception.Message
                continue
            }
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

    Invoke-Case 'renders all artifacts with independent byte-boundary and compiler-payload proof' {
        $packet = Get-TestPacket 'render-input' -AlternatingByteBoundaries
        $stalePath = $packet.Paths[0]
        $staleHasUtf8Bom = $packet.ByteBoundaries[0].HasUtf8Bom
        Write-TestText $stalePath (Replace-Once ([IO.File]::ReadAllText($stalePath)) '"assignmentDigest": "' + $script:Compiled.AssignmentDigest + '"' '"assignmentDigest": "0000000000000000000000000000000000000000000000000000000000000000"') -Utf8Bom:$staleHasUtf8Bom
        $before = Get-Hashes $packet.Paths
        $first = Join-Path $script:TestRoot 'render-one'
        $second = Join-Path $script:TestRoot 'render-two'
        $firstPaths = @(Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $first)
        $secondPaths = @(Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $second)
        Assert-True (($firstPaths.Count -eq $script:Compiled.Artifacts.Count) -and ($secondPaths.Count -eq $script:Compiled.Artifacts.Count)) 'both complete renders contain compiler artifacts'
        Assert-True ((Get-Hashes $packet.Paths) -join '|' -ceq ($before -join '|')) 'render leaves inputs byte-identical'
        Assert-True ((Get-Hashes $firstPaths) -join '|' -ceq ((Get-Hashes $secondPaths) -join '|')) 'complete renders are deterministic'
        Assert-True ($packet.ByteBoundaries.Count -eq 15) 'the complete packet has all required byte-boundary cases'
        Assert-True ((@($packet.ByteBoundaries | Where-Object { $_.HasUtf8Bom }).Count -gt 0) -and (@($packet.ByteBoundaries | Where-Object { -not $_.HasUtf8Bom }).Count -gt 0)) 'the complete packet explicitly covers UTF-8 BOM and no-BOM inputs'
        Assert-True ((@($packet.ByteBoundaries | Where-Object { $_.LineEnding -ceq "`n" }).Count -gt 0) -and (@($packet.ByteBoundaries | Where-Object { $_.LineEnding -ceq "`r`n" }).Count -gt 0)) 'the complete packet explicitly covers LF and CRLF boundaries'
        foreach ($boundary in $packet.ByteBoundaries) {
            $artifact = [IO.Path]::GetFileNameWithoutExtension($boundary.Path)
            $sourceBytes = [IO.File]::ReadAllBytes($boundary.Path)
            $hasBom = $sourceBytes.Length -ge 3 -and $sourceBytes[0] -eq 0xEF -and $sourceBytes[1] -eq 0xBB -and $sourceBytes[2] -eq 0xBF
            Assert-True ($hasBom -eq $boundary.HasUtf8Bom) "$artifact has its declared UTF-8 BOM boundary"
            if ($boundary.LineEnding -ceq "`r`n") {
                Assert-True ((Find-ByteSequence $sourceBytes ([byte[]]@(13, 10))) -ge 0) "$artifact has CRLF source boundaries"
            }
            else {
                Assert-True ((Find-ByteSequence $sourceBytes ([byte[]]@(13, 10))) -lt 0) "$artifact has LF-only source boundaries"
            }
            foreach ($renderedPath in @((Join-Path $first ($artifact + '.md')), (Join-Path $second ($artifact + '.md')))) {
                Assert-IndependentOutsideBytesMatch $boundary.Path $renderedPath $artifact
                Assert-IndependentPayloadMatchesCompiler $renderedPath $artifact
            }
        }
        Test-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $firstPaths | Out-Null

        $target = $firstPaths[0]
        $artifact = [IO.Path]::GetFileNameWithoutExtension($target)
        $sourcePath = @($packet.Paths | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_) -ceq $artifact })[0]
        $original = [IO.File]::ReadAllBytes($target)
        $span = Get-IndependentProjectionSpan $original $artifact $target

        $changed = [byte[]]$original.Clone()
        $changed[$span.StartIndex - 1] = $changed[$span.StartIndex - 1] -bxor 0x01
        [IO.File]::WriteAllBytes($target, $changed)
        Assert-Fails { Assert-IndependentOutsideBytesMatch $sourcePath $target $artifact } 'outside its marker pair'

        [IO.File]::WriteAllBytes($target, (Get-ByteSlice $original 0 ($original.Length - 1)))
        Assert-Fails { Assert-IndependentOutsideBytesMatch $sourcePath $target $artifact } 'outside its marker pair'
        [IO.File]::WriteAllBytes($target, $original)

        Assert-True ($original[0] -eq 0xEF -and $original[1] -eq 0xBB -and $original[2] -eq 0xBF) "$artifact mutation target has a UTF-8 BOM"
        [IO.File]::WriteAllBytes($target, (Get-ByteSlice $original 3 ($original.Length - 3)))
        Assert-Fails { Assert-IndependentOutsideBytesMatch $sourcePath $target $artifact } 'outside its marker pair'
        [IO.File]::WriteAllBytes($target, $original)

        $crLfBoundary = @($packet.ByteBoundaries | Where-Object { $_.LineEnding -ceq "`r`n" })[0]
        $crLfArtifact = [IO.Path]::GetFileNameWithoutExtension($crLfBoundary.Path)
        $crLfTarget = Join-Path $first ($crLfArtifact + '.md')
        $crLfOriginal = [IO.File]::ReadAllBytes($crLfTarget)
        Write-TestText $crLfTarget ([IO.File]::ReadAllText($crLfTarget).Replace("`r`n", "`n"))
        Assert-Fails { Assert-IndependentOutsideBytesMatch $crLfBoundary.Path $crLfTarget $crLfArtifact } 'outside its marker pair'
        [IO.File]::WriteAllBytes($crLfTarget, $crLfOriginal)

        $originalPayload = [IO.File]::ReadAllText($target)
        $mutations = @(
            @{ Name = 'schema'; Expected = 'fixed public schema'; Apply = { param($payload) $payload.schema = 'wrong.schema' } },
            @{ Name = 'artifact identity'; Expected = 'artifact identity'; Apply = { param($payload) $payload.artifact = 'wrong-artifact' } },
            @{ Name = 'canonical digest'; Expected = 'compiler canonical digest'; Apply = { param($payload) $payload.canonicalContentDigest = '0' * 64 } },
            @{ Name = 'assignment digest'; Expected = 'compiler assignment digest'; Apply = { param($payload) $payload.assignmentDigest = '0' * 64 } },
            @{ Name = 'row count'; Expected = 'compiler rowCount'; Apply = { param($payload) $payload.counts.rowCount++ } },
            @{ Name = 'applicability key count'; Expected = 'compiler applicabilityKeyCount'; Apply = { param($payload) $payload.counts.applicabilityKeyCount++ } },
            @{ Name = 'requirement count'; Expected = 'compiler requirementCount'; Apply = { param($payload) $payload.counts.requirementCount++ } },
            @{ Name = 'artifact count'; Expected = 'compiler artifactCount'; Apply = { param($payload) $payload.counts.artifactCount++ } },
            @{ Name = 'requirement omission'; Expected = 'every compiler requirement'; Apply = { param($payload) $payload.requirements = @($payload.requirements | Select-Object -Skip 1) } },
            @{ Name = 'requirement reordering'; Expected = 'compiler ID'; Apply = { param($payload) $items = @($payload.requirements); $payload.requirements = @($items[1], $items[0]) + @($items | Select-Object -Skip 2) } },
            @{ Name = 'requirement owner drift'; Expected = 'compiler owner'; Apply = { param($payload) $payload.requirements[0].owner = '#000' } },
            @{ Name = 'artifact omission'; Expected = 'ordered compiler artifact IDs'; Apply = { param($payload) $payload.artifactIds = @($payload.artifactIds | Select-Object -Skip 1) } },
            @{ Name = 'artifact reordering'; Expected = 'ordered compiler artifact IDs'; Apply = { param($payload) $items = @($payload.artifactIds); $payload.artifactIds = @($items[1], $items[0]) + @($items | Select-Object -Skip 2) } },
            @{ Name = 'artifact ID drift'; Expected = 'ordered compiler artifact IDs'; Apply = { param($payload) $payload.artifactIds[0] = 'wrong-artifact' } },
            @{ Name = 'assignment omission'; Expected = 'ordered compiler assignment row IDs'; Apply = { param($payload) $payload.assignment.rowIds = @($payload.assignment.rowIds | Select-Object -Skip 1) } },
            @{ Name = 'assignment reordering'; Expected = 'ordered compiler assignment row IDs'; Apply = { param($payload) $items = @($payload.assignment.rowIds); $payload.assignment.rowIds = @($items[1], $items[0]) + @($items | Select-Object -Skip 2) } },
            @{ Name = 'assignment row drift'; Expected = 'ordered compiler assignment row IDs'; Apply = { param($payload) $payload.assignment.rowIds[0] = 'WDU-LC-000' } }
        )
        foreach ($mutation in $mutations) {
            Write-TestText $target $originalPayload
            Set-TestPayloadMutation $target $artifact $mutation.Apply
            Assert-Fails { Assert-IndependentPayloadMatchesCompiler $target $artifact } $mutation.Expected
        }
        Write-TestText $target $originalPayload
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $first } 'render destination already exists'
    }

    Invoke-Case 'rejects a partial packet before creating an output directory' {
        $packet = Get-TestPacket 'render-preflight'
        $destination = Join-Path $script:TestRoot 'must-not-exist'
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths[0..($packet.Paths.Count - 2)] -OutputDirectory $destination } 'packet is missing required artifact'
        Assert-True (-not (Test-Path -LiteralPath $destination)) 'failed preflight creates no destination'
    }

    Invoke-Case 'rejects lexical and junction aliases inside an input packet before writes' {
        $packet = Get-TestPacket 'destination-aliases'
        $before = Get-Hashes $packet.Paths
        $lexicalDestination = Join-Path $packet.Directory '.\rendered'
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $lexicalDestination } 'render destination must not be within an input artifact directory'
        Assert-True (-not (Test-Path -LiteralPath $lexicalDestination)) 'lexical alias creates no destination'

        $junction = Join-Path $script:TestRoot 'input-packet-junction'
        New-Item -ItemType Junction -Path $junction -Target $packet.Directory | Out-Null
        $junctionDestination = Join-Path $junction 'rendered'
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $junctionDestination } 'render destination must not be within an input artifact directory'
        Assert-True (-not (Test-Path -LiteralPath $junctionDestination)) 'junction alias creates no destination'
        Assert-True ((Get-Hashes $packet.Paths) -join '|' -ceq ($before -join '|')) 'alias rejection leaves inputs byte-identical'
    }

    Invoke-Case 'cleans staged writes after deterministic late failure without changing inputs' {
        $packet = Get-TestPacket 'late-failure'
        $before = Get-Hashes $packet.Paths
        $destination = Join-Path $script:TestRoot 'late-failure-output'
        Set-TestStagedWriteFailure 1
        try {
            Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonicalPath -ArtifactPath $packet.Paths -OutputDirectory $destination } 'test-only deterministic failure after staged write'
        }
        finally {
            Clear-TestStagedWriteFailure
        }
        $stagingPattern = '.' + [IO.Path]::GetFileName($destination) + '.staging-*'
        $staging = @(Get-ChildItem -LiteralPath $script:TestRoot -Directory -Filter $stagingPattern)
        Assert-True ((Get-Hashes $packet.Paths) -join '|' -ceq ($before -join '|')) 'late failure leaves inputs byte-identical'
        Assert-True (-not (Test-Path -LiteralPath $destination)) 'late failure creates no final destination'
        Assert-True ($staging.Count -eq 0) 'late failure removes its staging directory'
    }
}
finally {
    if (Test-Path -LiteralPath $script:TestRoot) { Remove-Item -LiteralPath $script:TestRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
