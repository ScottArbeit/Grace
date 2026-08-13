Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'WduLifecycleContract.psm1') -Force

$script:MarkerPrefix = '<!-- grace:wdu-lifecycle-projection:'
$script:ProjectionSchema = 'grace.wdu.lifecycle-projection/v2'
$script:Utf8 = [Text.UTF8Encoding]::new($false, $true)

function Fail-Projection {
    param([string] $Subject, [string] $Reason)
    throw "WDU lifecycle projection '$Subject': $Reason"
}

function Normalize-LineEndings {
    param([string] $Text)
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-ProjectionMarker {
    param([string] $Artifact, [ValidateSet('start', 'end')][string] $Kind)
    return "<!-- grace:wdu-lifecycle-projection:$Artifact`:$Kind -->"
}

function Get-ProjectionLineEnding {
    param([string] $Text)
    if ($Text.Contains("`r`n", [StringComparison]::Ordinal)) { return "`r`n" }
    if ($Text.Contains("`r", [StringComparison]::Ordinal)) { return "`r" }
    return "`n"
}

function Get-CompiledArtifactMap {
    param([object] $Compiled)
    $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($artifact in $Compiled.Artifacts) {
        if (-not $map.TryAdd($artifact.Id, $artifact)) {
            Fail-Projection $Compiled.Digest "compiler returned duplicate artifact '$($artifact.Id)'"
        }
    }
    return $map
}

function Get-ProjectionPairs {
    param([string] $Text, [object] $Compiled, [string] $Subject)

    $artifactMap = Get-CompiledArtifactMap $Compiled
    $tokens = [Collections.Generic.List[object]]::new()
    $matches = [regex]::Matches($Text, '<!-- grace:wdu-lifecycle-projection:(?<artifact>[a-z0-9-]+):(?<kind>start|end) -->')
    $prefixCount = 0
    $offset = 0
    while ($offset -lt $Text.Length) {
        $index = $Text.IndexOf($script:MarkerPrefix, $offset, [StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $prefixCount++
        $offset = $index + $script:MarkerPrefix.Length
    }
    if ($prefixCount -ne $matches.Count) { Fail-Projection $Subject 'contains a malformed lifecycle projection marker' }
    foreach ($match in $matches) {
        $tokens.Add([pscustomobject]@{
                Artifact = $match.Groups['artifact'].Value
                Kind = $match.Groups['kind'].Value
                Position = $match.Index
            })
    }

    if ($tokens.Count -eq 0) { Fail-Projection $Subject 'requires one artifact-specific projection marker pair' }
    $starts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ends = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pairs = [Collections.Generic.List[object]]::new()
    $open = $null
    foreach ($token in $tokens) {
        if (-not $artifactMap.ContainsKey($token.Artifact)) {
            Fail-Projection $Subject "contains unknown artifact marker '$($token.Artifact)'"
        }
        if ($token.Kind -eq 'start') {
            if ($null -ne $open) { Fail-Projection $Subject 'contains nested lifecycle projection markers' }
            if (-not $starts.Add($token.Artifact)) { Fail-Projection $Subject "contains duplicate start marker for '$($token.Artifact)'" }
            $open = $token
            continue
        }
        if ($null -eq $open) { Fail-Projection $Subject "contains reversed end marker for '$($token.Artifact)'" }
        if (-not [StringComparer]::Ordinal.Equals($open.Artifact, $token.Artifact)) {
            Fail-Projection $Subject "contains mismatched marker pair '$($open.Artifact)' and '$($token.Artifact)'"
        }
        if (-not $ends.Add($token.Artifact)) { Fail-Projection $Subject "contains duplicate end marker for '$($token.Artifact)'" }
        $pairs.Add([pscustomobject]@{ Artifact = $token.Artifact })
        $open = $null
    }
    if ($null -ne $open) { Fail-Projection $Subject "is missing end marker for '$($open.Artifact)'" }
    if ($pairs.Count -ne 1) { Fail-Projection $Subject 'requires exactly one artifact-specific projection marker pair' }

    $artifact = $pairs[0].Artifact
    $startMarker = Get-ProjectionMarker $artifact start
    $endMarker = Get-ProjectionMarker $artifact end
    $startIndex = $Text.IndexOf($startMarker, [StringComparison]::Ordinal)
    $endIndex = $Text.IndexOf($endMarker, [StringComparison]::Ordinal)
    if ($startIndex -lt 0 -or $endIndex -le $startIndex) { Fail-Projection $Subject "has malformed marker pair for '$artifact'" }
    return [pscustomobject]@{
        Artifact = $artifact
        StartIndex = $startIndex
        EndIndex = $endIndex
        EndLength = $endMarker.Length
        Block = $Text.Substring($startIndex, ($endIndex + $endMarker.Length) - $startIndex)
    }
}

function Get-ProjectionPayload {
    param([object] $Compiled, [object] $Artifact)
    $requirements = @($Compiled.Requirements | ForEach-Object {
            [ordered]@{ id = $_.Id; owner = $_.Owner }
        })
    $artifactIds = @($Compiled.Artifacts | ForEach-Object { $_.Id })
    return [ordered]@{
        schema = $script:ProjectionSchema
        artifact = $Artifact.Id
        canonicalContentDigest = $Compiled.Digest
        assignmentDigest = $Compiled.AssignmentDigest
        counts = [ordered]@{
            rowCount = $Compiled.Counts.rowCount
            applicabilityKeyCount = $Compiled.Counts.applicabilityKeyCount
            requirementCount = $Compiled.Counts.requirementCount
            artifactCount = $Compiled.Counts.artifactCount
        }
        requirements = $requirements
        artifactIds = $artifactIds
        assignment = [ordered]@{ rowIds = @($Artifact.RowIds) }
    }
}

function New-ProjectionText {
    param([object] $Compiled, [object] $Artifact)
    $json = Normalize-LineEndings (Get-ProjectionPayload $Compiled $Artifact | ConvertTo-Json -Depth 8)
    return (Get-ProjectionMarker $Artifact.Id start) + "`n" + '```json' + "`n" + $json + "`n" + '```' + "`n" + (Get-ProjectionMarker $Artifact.Id end)
}

function Get-Artifact {
    param([object] $Compiled, [string] $Artifact, [string] $Subject)
    $map = Get-CompiledArtifactMap $Compiled
    if (-not $map.ContainsKey($Artifact)) { Fail-Projection $Subject "has unknown artifact '$Artifact'" }
    return $map[$Artifact]
}

function Get-PacketInputs {
    param([object] $Compiled, [string[]] $ArtifactPath, [switch] $RequireExact)

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inputs = [Collections.Generic.List[object]]::new()
    foreach ($path in $ArtifactPath) {
        $resolved = [IO.Path]::GetFullPath($path)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { Fail-Projection $resolved 'does not exist as an artifact file' }
        $text = [IO.File]::ReadAllText($resolved)
        $pair = Get-ProjectionPairs $text $Compiled $resolved
        $expectedFileName = $pair.Artifact + '.md'
        if (-not [StringComparer]::Ordinal.Equals([IO.Path]::GetFileName($resolved), $expectedFileName)) {
            Fail-Projection $resolved "artifact path name must equal '$expectedFileName'"
        }
        if (-not $seen.Add($pair.Artifact)) { Fail-Projection $resolved "packet contains duplicate artifact '$($pair.Artifact)'" }
        $artifact = Get-Artifact $Compiled $pair.Artifact $resolved
        $expected = New-ProjectionText $Compiled $artifact
        if ($RequireExact -and -not [StringComparer]::Ordinal.Equals((Normalize-LineEndings $pair.Block), $expected)) {
            Fail-Projection $resolved "artifact '$($pair.Artifact)' does not equal its exact generated projection"
        }
        $inputs.Add([pscustomobject]@{ Path = $resolved; Text = $text; Pair = $pair; Artifact = $artifact; Expected = $expected })
    }
    foreach ($artifact in $Compiled.Artifacts) {
        if (-not $seen.Contains($artifact.Id)) { Fail-Projection $Compiled.Digest "packet is missing required artifact '$($artifact.Id)'" }
    }
    return $inputs.ToArray()
}

function Get-Utf8BomLength {
    param([byte[]] $Bytes)
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { return 3 }
    return 0
}

function Write-RenderedArtifact {
    param([object] $ArtifactInput, [string] $Destination)
    $bytes = [IO.File]::ReadAllBytes($ArtifactInput.Path)
    $bomLength = Get-Utf8BomLength $bytes
    $prefix = $ArtifactInput.Text.Substring(0, $ArtifactInput.Pair.StartIndex)
    $suffixStart = $ArtifactInput.Pair.EndIndex + $ArtifactInput.Pair.EndLength
    $suffix = $ArtifactInput.Text.Substring($suffixStart)
    $lineEnding = Get-ProjectionLineEnding $ArtifactInput.Text
    $replacement = $ArtifactInput.Expected.Replace("`n", $lineEnding)
    $prefixBytes = $script:Utf8.GetBytes($prefix)
    $replacementBytes = $script:Utf8.GetBytes($replacement)
    $suffixBytes = $script:Utf8.GetBytes($suffix)
    $result = [byte[]]::new($bomLength + $prefixBytes.Length + $replacementBytes.Length + $suffixBytes.Length)
    if ($bomLength -gt 0) { [Array]::Copy($bytes, 0, $result, 0, $bomLength) }
    [Array]::Copy($prefixBytes, 0, $result, $bomLength, $prefixBytes.Length)
    [Array]::Copy($replacementBytes, 0, $result, $bomLength + $prefixBytes.Length, $replacementBytes.Length)
    [Array]::Copy($suffixBytes, 0, $result, $bomLength + $prefixBytes.Length + $replacementBytes.Length, $suffixBytes.Length)
    [IO.File]::WriteAllBytes($Destination, $result)
}

function New-WduLifecycleProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $CanonicalPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Artifact
    )
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    return New-ProjectionText $compiled (Get-Artifact $compiled $Artifact $CanonicalPath)
}

function Test-WduLifecycleProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $CanonicalPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]] $ArtifactPath
    )
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $inputs = @(Get-PacketInputs $compiled $ArtifactPath -RequireExact)
    foreach ($artifact in $compiled.Artifacts) {
        $artifactInput = $null
        foreach ($candidate in $inputs) {
            if ([StringComparer]::Ordinal.Equals($candidate.Artifact.Id, $artifact.Id)) {
                $artifactInput = $candidate
                break
            }
        }
        if ($null -eq $artifactInput) { Fail-Projection $Compiled.Digest "packet is missing required artifact '$($artifact.Id)'" }
        [pscustomobject]@{ Artifact = $artifact.Id; Path = $artifactInput.Path; Result = 'ExactMatch' }
    }
}

function Export-WduLifecycleProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $CanonicalPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]] $ArtifactPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OutputDirectory
    )
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $inputs = @(Get-PacketInputs $compiled $ArtifactPath)
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $destination) { Fail-Projection $destination 'render destination already exists' }
    $parent = [IO.Directory]::GetParent($destination)
    if ($null -eq $parent -or -not $parent.Exists) { Fail-Projection $destination 'render destination parent does not exist' }
    $staging = Join-Path $parent.FullName ('.' + [IO.Path]::GetFileName($destination) + '.staging-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        foreach ($artifactInput in $inputs) {
            Write-RenderedArtifact $artifactInput (Join-Path $staging ($artifactInput.Artifact.Id + '.md'))
        }
        Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
    }
    catch {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        throw
    }
    return Get-ChildItem -LiteralPath $destination -File | Sort-Object Name | Select-Object -ExpandProperty FullName
}

Export-ModuleMember -Function New-WduLifecycleProjection, Test-WduLifecycleProjection, Export-WduLifecycleProjection
