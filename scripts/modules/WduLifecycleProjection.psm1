Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'WduLifecycleContract.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WduLifecycleRawProjection.psm1') -Force

$script:Utf8 = [Text.UTF8Encoding]::new($false, $true)
$script:MarkerPrefix = '<!-- grace:wdu-lifecycle-projection:'
$script:FailureAfterStagedWritesForTest = $null

function Fail-Packet {
    param([string] $Subject, [string] $Reason)
    throw "WDU lifecycle packet '$Subject': $Reason"
}

function Get-Marker {
    param([string] $Artifact, [ValidateSet('start', 'end')][string] $Kind)
    return "<!-- grace:wdu-lifecycle-projection:$Artifact`:$Kind -->"
}

function Find-Bytes {
    param([byte[]] $Bytes, [byte[]] $Needle, [int] $Offset = 0)
    for ($index = $Offset; $index -le $Bytes.Length - $Needle.Length; $index++) {
        $same = $true
        for ($needleIndex = 0; $needleIndex -lt $Needle.Length; $needleIndex++) {
            if ($Bytes[$index + $needleIndex] -ne $Needle[$needleIndex]) { $same = $false; break }
        }
        if ($same) { return $index }
    }
    return -1
}

function Get-LineEndingBytes {
    param([byte[]] $Bytes)
    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -eq 13 -and $index + 1 -lt $Bytes.Length -and $Bytes[$index + 1] -eq 10) { return [byte[]](13, 10) }
        if ($Bytes[$index] -eq 10) { return [byte[]](10) }
    }
    return [byte[]](10)
}

function Get-ArtifactMap {
    param([object] $Compiled)
    $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($artifact in $Compiled.Artifacts) {
        if (-not $map.TryAdd($artifact.Id, $artifact)) { Fail-Packet $Compiled.Digest "compiler returned duplicate artifact '$($artifact.Id)'" }
    }
    return $map
}

function Get-MarkerPair {
    param([byte[]] $Bytes, [object] $Compiled, [string] $Subject)
    $prefix = $script:Utf8.GetBytes($script:MarkerPrefix)
    $endComment = $script:Utf8.GetBytes('-->')
    $tokens = [Collections.Generic.List[object]]::new()
    $offset = 0
    while ($true) {
        $start = Find-Bytes $Bytes $prefix $offset
        if ($start -lt 0) { break }
        $commentEnd = Find-Bytes $Bytes $endComment ($start + $prefix.Length)
        if ($commentEnd -lt 0) { Fail-Packet $Subject 'contains malformed lifecycle projection marker' }
        $marker = $script:Utf8.GetString($Bytes, $start, $commentEnd + $endComment.Length - $start)
        if ($marker -notmatch '^<!-- grace:wdu-lifecycle-projection:([a-z0-9-]+):(start|end) -->$') { Fail-Packet $Subject 'contains malformed or generic legacy lifecycle projection marker' }
        $tokens.Add([pscustomobject]@{ Artifact = $Matches[1]; Kind = $Matches[2]; Start = $start; End = $commentEnd + $endComment.Length })
        $offset = $commentEnd + $endComment.Length
    }
    if ($tokens.Count -eq 0) { Fail-Packet $Subject 'requires one artifact-specific projection marker pair' }
    $map = Get-ArtifactMap $Compiled
    $open = $null
    $pairs = [Collections.Generic.List[object]]::new()
    $starts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ends = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($token in $tokens) {
        if (-not $map.ContainsKey($token.Artifact)) { Fail-Packet $Subject "contains unknown artifact marker '$($token.Artifact)'" }
        if ($token.Kind -eq 'start') {
            if ($null -ne $open) { Fail-Packet $Subject 'contains nested lifecycle projection markers' }
            if (-not $starts.Add($token.Artifact)) { Fail-Packet $Subject "contains duplicate start marker for '$($token.Artifact)'" }
            $open = $token
        }
        else {
            if ($null -eq $open) { Fail-Packet $Subject "contains reversed end marker for '$($token.Artifact)'" }
            if (-not [StringComparer]::Ordinal.Equals($open.Artifact, $token.Artifact)) { Fail-Packet $Subject "contains mismatched marker pair '$($open.Artifact)' and '$($token.Artifact)'" }
            if (-not $ends.Add($token.Artifact)) { Fail-Packet $Subject "contains duplicate end marker for '$($token.Artifact)'" }
            $pairs.Add([pscustomobject]@{ Artifact = $token.Artifact; Start = $open.Start; ContentStart = $open.End; ContentEnd = $token.Start; End = $token.End })
            $open = $null
        }
    }
    if ($null -ne $open) { Fail-Packet $Subject "is missing end marker for '$($open.Artifact)'" }
    if ($pairs.Count -ne 1) { Fail-Packet $Subject 'requires exactly one artifact-specific projection marker pair' }
    return $pairs[0]
}

function Get-ExpectedBlockBytes {
    param([object] $Compiled, [object] $Artifact, [byte[]] $LineEnding)
    $payload = New-WduLifecycleRawProjection -Compiled $Compiled -Artifact $Artifact.Id
    $parts = [Collections.Generic.List[byte[]]]::new()
    $parts.Add($script:Utf8.GetBytes((Get-Marker $Artifact.Id start)))
    $parts.Add($LineEnding)
    $parts.Add($script:Utf8.GetBytes('```json'))
    $parts.Add($LineEnding)
    $parts.Add($payload.Utf8Json.ToArray())
    $parts.Add($LineEnding)
    $parts.Add($script:Utf8.GetBytes('```'))
    $parts.Add($LineEnding)
    $parts.Add($script:Utf8.GetBytes((Get-Marker $Artifact.Id end)))
    $length = 0
    foreach ($part in $parts) { $length += $part.Length }
    $result = [byte[]]::new($length)
    $position = 0
    foreach ($part in $parts) { [Array]::Copy($part, 0, $result, $position, $part.Length); $position += $part.Length }
    return ,$result
}

function Get-PacketInputs {
    param([object] $Compiled, [string] $PacketDirectory, [switch] $RequireExact)
    $directory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($PacketDirectory))
    if (-not $directory.Exists) { Fail-Packet $directory.FullName 'does not exist as a packet directory' }
    $files = @($directory.GetFiles('*.md') | Sort-Object Name)
    $map = Get-ArtifactMap $Compiled
    if ($files.Count -ne $map.Count) { Fail-Packet $directory.FullName "must contain exactly $($map.Count) packet files" }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inputs = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $bom = if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { 3 } else { 0 }
        try { $null = $script:Utf8.GetString($bytes, $bom, $bytes.Length - $bom) } catch { Fail-Packet $file.FullName 'must be valid UTF-8 Markdown' }
        $pair = Get-MarkerPair $bytes $Compiled $file.FullName
        $expectedName = "$($pair.Artifact).md"
        if (-not [StringComparer]::Ordinal.Equals($file.Name, $expectedName)) { Fail-Packet $file.FullName "artifact path name must equal '$expectedName'" }
        if (-not $seen.Add($pair.Artifact)) { Fail-Packet $file.FullName "packet contains duplicate physical artifact '$($pair.Artifact)'" }
        $artifact = $map[$pair.Artifact]
        $expected = Get-ExpectedBlockBytes $Compiled $artifact (Get-LineEndingBytes $bytes)
        if ($RequireExact) {
            $actual = $bytes[$pair.Start..($pair.End - 1)]
            if ([Convert]::ToHexString($actual) -cne [Convert]::ToHexString($expected)) { Fail-Packet $file.FullName "artifact '$($pair.Artifact)' does not equal the exact raw projection block" }
        }
        $inputs.Add([pscustomobject]@{ File = $file; Bytes = $bytes; Pair = $pair; Artifact = $artifact; Expected = $expected })
    }
    foreach ($artifact in $Compiled.Artifacts) { if (-not $seen.Contains($artifact.Id)) { Fail-Packet $directory.FullName "packet is missing required artifact '$($artifact.Id)'" } }
    return @($inputs)
}

function Test-WduLifecycleProjection {
    param([Parameter(Mandatory)][string] $CanonicalPath, [Parameter(Mandatory)][string] $PacketDirectory)
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $inputs = @(Get-PacketInputs $compiled $PacketDirectory -RequireExact)
    foreach ($artifact in $compiled.Artifacts) {
        $input = @($inputs | Where-Object { [StringComparer]::Ordinal.Equals($_.Artifact.Id, $artifact.Id) })[0]
        [pscustomobject]@{ Artifact = $artifact.Id; Path = $input.File.FullName; Result = 'ExactMatch' }
    }
}

function Export-WduLifecycleProjection {
    param([Parameter(Mandatory)][string] $CanonicalPath, [Parameter(Mandatory)][string] $PacketDirectory, [Parameter(Mandatory)][string] $OutputDirectory)
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $inputs = @(Get-PacketInputs $compiled $PacketDirectory)
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $destination) { Fail-Packet $destination 'render destination already exists' }
    $parent = [IO.Directory]::GetParent($destination)
    if ($null -eq $parent -or -not $parent.Exists) { Fail-Packet $destination 'render destination parent does not exist' }
    foreach ($input in $inputs) { if ($destination.StartsWith([IO.Path]::GetDirectoryName($input.File.FullName) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { Fail-Packet $destination 'render destination must not be within an input artifact directory' } }
    $staging = Join-Path $parent.FullName ('.' + [IO.Path]::GetFileName($destination) + '.staging-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        $writes = 0
        foreach ($input in $inputs) {
            [byte[]] $prefix = [byte[]]::new(0)
            if ($input.Pair.Start -gt 0) { $prefix = $input.Bytes[0..($input.Pair.Start - 1)] }
            [byte[]] $suffix = [byte[]]::new(0)
            if ($input.Pair.End -lt $input.Bytes.Length) { $suffix = $input.Bytes[$input.Pair.End..($input.Bytes.Length - 1)] }
            [byte[]] $expected = $input.Expected
            $output = [byte[]]::new($prefix.Length + $expected.Length + $suffix.Length)
            [Array]::Copy($prefix, 0, $output, 0, $prefix.Length)
            [Array]::Copy($expected, 0, $output, $prefix.Length, $expected.Length)
            [Array]::Copy($suffix, 0, $output, $prefix.Length + $expected.Length, $suffix.Length)
            [IO.File]::WriteAllBytes((Join-Path $staging "$($input.Artifact.Id).md"), $output)
            $writes++
            if ($null -ne $script:FailureAfterStagedWritesForTest -and $writes -ge $script:FailureAfterStagedWritesForTest) { throw 'test-only deterministic failure after staged write' }
        }
        Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
    }
    catch { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }; throw }
    return @(Get-ChildItem -LiteralPath $destination -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
}

Export-ModuleMember -Function Test-WduLifecycleProjection, Export-WduLifecycleProjection
