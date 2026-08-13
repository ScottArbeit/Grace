Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'WduLifecycleContract.psm1') -Force

$script:Utf8 = [Text.UTF8Encoding]::new($false, $true)
$script:MarkerSentinel = 'grace:wdu-lifecycle-projection'
$script:RawExportCommand = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../get-wdu-lifecycle-projection.ps1'))
$script:FailureAfterStagedWritesForTest = $null

function Fail-Packet {
    param([string] $Subject, [string] $Reason)

    throw "WDU lifecycle packet '$Subject': $Reason"
}

function Get-Marker {
    param([string] $Artifact, [ValidateSet('start', 'end')][string] $Kind)

    "<!-- grace:wdu-lifecycle-projection:$Artifact`:$Kind -->"
}

function Find-Bytes {
    param([byte[]] $Bytes, [byte[]] $Needle, [int] $Offset = 0)

    for ($index = $Offset; $index -le $Bytes.Length - $Needle.Length; $index++) {
        $same = $true
        for ($needleIndex = 0; $needleIndex -lt $Needle.Length; $needleIndex++) {
            if ($Bytes[$index + $needleIndex] -ne $Needle[$needleIndex]) {
                $same = $false
                break
            }
        }
        if ($same) { return $index }
    }

    -1
}

function Get-LineEndingBytes {
    param([byte[]] $Bytes)

    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -eq 13 -and $index + 1 -lt $Bytes.Length -and $Bytes[$index + 1] -eq 10) {
            return [byte[]](13, 10)
        }
        if ($Bytes[$index] -eq 10) { return [byte[]](10) }
    }

    [byte[]](10)
}

function Get-ArtifactMap {
    param([object] $Compiled)

    $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($artifact in $Compiled.Artifacts) {
        if (-not $map.TryAdd($artifact.Id, $artifact)) {
            Fail-Packet $Compiled.Digest "compiler returned duplicate artifact '$($artifact.Id)'"
        }
    }

    $map
}

function Start-WduLifecycleRawExportProcess {
    param(
        [Parameter(Mandatory)][string] $CanonicalPath,
        [Parameter(Mandatory)][string] $Artifact,
        [Parameter(Mandatory)][string] $RawExportCommand
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh -CommandType Application).Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $RawExportCommand, '-CanonicalPath', $CanonicalPath, '-Artifact', $Artifact)) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void] $process.Start()
        $stdout = [IO.MemoryStream]::new()
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        return [pscustomobject]@{ Artifact = $Artifact; Process = $process; Stdout = $stdout; StdoutTask = $stdoutTask; StderrTask = $stderrTask }
    }
    catch {
        $process.Dispose()
        throw
    }
}

function Get-WduLifecycleRawExportBytes {
    param(
        [Parameter(Mandatory)][string] $CanonicalPath,
        [Parameter(Mandatory)][string[]] $Artifacts,
        [Parameter(Mandatory)][string] $RawExportCommand
    )

    $payloads = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    foreach ($batch in @($Artifacts | ForEach-Object -Begin { $items = [Collections.Generic.List[string]]::new() } -Process {
            $items.Add($_)
            if ($items.Count -eq 4) { ,@($items); $items.Clear() }
        } -End { if ($items.Count -gt 0) { ,@($items) } })) {
        $operations = [Collections.Generic.List[object]]::new()
        try {
            foreach ($artifact in $batch) {
                $operations.Add((Start-WduLifecycleRawExportProcess -CanonicalPath $CanonicalPath -Artifact $artifact -RawExportCommand $RawExportCommand))
            }
        foreach ($operation in $operations) {
            $operation.Process.WaitForExit()
            $null = $operation.StdoutTask.GetAwaiter().GetResult()
            $stderr = $operation.StderrTask.GetAwaiter().GetResult()
            if ($operation.Process.ExitCode -ne 0) {
                Fail-Packet $operation.Artifact "raw export command failed with exit code $($operation.Process.ExitCode): $stderr"
            }
            if (-not [string]::IsNullOrEmpty($stderr)) {
                Fail-Packet $operation.Artifact "raw export command wrote stderr: $stderr"
            }
            if (-not $payloads.TryAdd($operation.Artifact, $operation.Stdout.ToArray())) {
                Fail-Packet $operation.Artifact 'raw export command received a duplicate artifact request'
            }
        }
        }
        finally {
            foreach ($operation in $operations) {
                $operation.Stdout.Dispose()
                $operation.Process.Dispose()
            }
        }
    }

    $payloads
}

function Get-MarkerTokens {
    param([byte[]] $Bytes, [string] $Subject)

    $commentStart = [byte[]](60, 33, 45, 45)
    $commentEnd = [byte[]](45, 45, 62)
    $tokens = [Collections.Generic.List[object]]::new()
    $offset = 0
    while ($true) {
        $start = Find-Bytes $Bytes $commentStart $offset
        if ($start -lt 0) { break }
        $end = Find-Bytes $Bytes $commentEnd ($start + $commentStart.Length)
        if ($end -lt 0) {
            $partial = $script:Utf8.GetString($Bytes, $start, $Bytes.Length - $start)
            if ($partial.IndexOf($script:MarkerSentinel, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Fail-Packet $Subject 'contains malformed lifecycle projection marker'
            }
            break
        }

        $comment = $script:Utf8.GetString($Bytes, $start, $end + $commentEnd.Length - $start)
        if ($comment.IndexOf($script:MarkerSentinel, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            if ($comment -cmatch '^<!-- grace:wdu-lifecycle-projection:(start|end) -->$') {
                Fail-Packet $Subject 'contains generic legacy lifecycle projection marker'
            }
            if ($comment -match '^<!-- grace:wdu-lifecycle-projection:(start|end) -->$') {
                Fail-Packet $Subject 'contains case-changed lifecycle projection marker'
            }
            if ($comment -cmatch '^<!-- grace:wdu-lifecycle-projection:([a-z0-9-]+):(start|end) -->$') {
                $tokens.Add([pscustomobject]@{ Artifact = $Matches[1]; Kind = $Matches[2]; Start = $start; End = $end + $commentEnd.Length })
            }
            elseif ($comment -match '^<!-- grace:wdu-lifecycle-projection:([a-z0-9-]+):(start|end) -->$') {
                Fail-Packet $Subject 'contains case-changed lifecycle projection marker'
            }
            else {
                Fail-Packet $Subject 'contains malformed lifecycle projection marker'
            }
        }
        $offset = $end + $commentEnd.Length
    }

    @($tokens)
}

function Get-MarkerPair {
    param([byte[]] $Bytes, [object] $Compiled, [string] $Subject)

    $tokens = @(Get-MarkerTokens $Bytes $Subject)
    if ($tokens.Count -eq 0) { Fail-Packet $Subject 'is missing lifecycle projection marker evidence' }
    $map = Get-ArtifactMap $Compiled
    foreach ($token in $tokens) {
        if (-not $map.ContainsKey($token.Artifact)) {
            Fail-Packet $Subject "contains unknown artifact marker '$($token.Artifact)'"
        }
    }
    foreach ($artifactId in $map.Keys) {
        $starts = @($tokens | Where-Object { $_.Artifact -ceq $artifactId -and $_.Kind -ceq 'start' })
        $ends = @($tokens | Where-Object { $_.Artifact -ceq $artifactId -and $_.Kind -ceq 'end' })
        if ($starts.Count -gt 1) { Fail-Packet $Subject "contains duplicate start marker for '$artifactId'" }
        if ($ends.Count -gt 1) { Fail-Packet $Subject "contains duplicate end marker for '$artifactId'" }
    }
    if ($tokens.Count -eq 1 -and $tokens[0].Kind -eq 'end') {
        Fail-Packet $Subject "is missing start marker for '$($tokens[0].Artifact)'"
    }

    $open = $null
    $pairs = [Collections.Generic.List[object]]::new()
    foreach ($token in $tokens) {
        if ($token.Kind -eq 'start') {
            if ($null -ne $open) { Fail-Packet $Subject 'contains nested lifecycle projection markers' }
            $open = $token
            continue
        }
        if ($null -eq $open) { Fail-Packet $Subject "contains reversed end marker for '$($token.Artifact)'" }
        if (-not [StringComparer]::Ordinal.Equals($open.Artifact, $token.Artifact)) {
            Fail-Packet $Subject "contains mismatched marker pair '$($open.Artifact)' and '$($token.Artifact)'"
        }
        $pairs.Add([pscustomobject]@{ Artifact = $open.Artifact; Start = $open.Start; End = $token.End })
        $open = $null
    }
    if ($null -ne $open) { Fail-Packet $Subject "is missing end marker for '$($open.Artifact)'" }
    if ($pairs.Count -ne 1) { Fail-Packet $Subject 'contains multiple artifact-specific projection marker pairs' }

    $pairs[0]
}

function Get-ExpectedBlockBytes {
    param(
        [string] $Artifact,
        [byte[]] $LineEnding,
        [byte[]] $Payload
    )

    $parts = [Collections.Generic.List[byte[]]]::new()
    $parts.Add($script:Utf8.GetBytes((Get-Marker $Artifact start)))
    $parts.Add($LineEnding)
    $parts.Add($script:Utf8.GetBytes('```json'))
    $parts.Add($LineEnding)
    $parts.Add($Payload)
    $parts.Add($LineEnding)
    $parts.Add($script:Utf8.GetBytes('```'))
    $parts.Add($LineEnding)
    $parts.Add($script:Utf8.GetBytes((Get-Marker $Artifact end)))
    $length = 0
    foreach ($part in $parts) { $length += $part.Length }
    $result = [byte[]]::new($length)
    $position = 0
    foreach ($part in $parts) {
        [Array]::Copy($part, 0, $result, $position, $part.Length)
        $position += $part.Length
    }

    ,$result
}

function Resolve-ExistingDirectory {
    param([string] $Path, [string] $Subject)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Directory]::Exists($fullPath)) { Fail-Packet $Subject 'does not exist as a directory' }
    $root = [IO.Path]::GetPathRoot($fullPath)
    $current = $root
    $relative = $fullPath.Substring($root.Length).Trim([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($relative.Length -eq 0) { return $current }
    foreach ($part in $relative -split '[\\/]') {
        $candidate = Join-Path $current $part
        $directory = [IO.DirectoryInfo]::new($candidate)
        if (-not $directory.Exists) { Fail-Packet $Subject "directory component '$part' does not exist" }
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $target = $directory.ResolveLinkTarget($true)
            if ($null -eq $target) { Fail-Packet $Subject "cannot resolve reparse-point directory component '$part'" }
            $current = [IO.Path]::GetFullPath($target.FullName)
        }
        else {
            $current = $directory.FullName
        }
    }

    $current
}

function Test-PathWithin {
    param([string] $Candidate, [string] $Directory)

    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    $directoryFull = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidateFull.StartsWith($directoryFull, [StringComparison]::OrdinalIgnoreCase)
}

function Get-PacketInputs {
    param(
        [object] $Compiled,
        [string] $CanonicalPath,
        [string] $PacketDirectory,
        [string] $RawExportCommand,
        [switch] $RequireExact
    )

    $lexicalDirectory = [IO.Path]::GetFullPath($PacketDirectory)
    $physicalDirectory = Resolve-ExistingDirectory $lexicalDirectory $lexicalDirectory
    $directory = [IO.DirectoryInfo]::new($physicalDirectory)
    $map = Get-ArtifactMap $Compiled
    $entries = @($directory.GetFileSystemInfos() | Sort-Object Name)
    if ($entries.Count -ne $map.Count) { Fail-Packet $lexicalDirectory "must contain exactly $($map.Count) packet members" }
    $seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inputs = [Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        if ($entry -isnot [IO.FileInfo]) { Fail-Packet $entry.FullName 'packet member must be one declared Markdown file' }
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail-Packet $entry.FullName 'packet member must not be a physical alias' }
        if (-not $seenNames.Add($entry.Name)) { Fail-Packet $entry.FullName "packet contains duplicate physical member '$($entry.Name)'" }
        $bytes = [IO.File]::ReadAllBytes($entry.FullName)
        $bom = if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { 3 } else { 0 }
        try { $null = $script:Utf8.GetString($bytes, $bom, $bytes.Length - $bom) } catch { Fail-Packet $entry.FullName 'must be valid UTF-8 Markdown' }
        $pair = Get-MarkerPair $bytes $Compiled $entry.FullName
        $expectedName = "$($pair.Artifact).md"
        if (-not [StringComparer]::Ordinal.Equals($entry.Name, $expectedName)) { Fail-Packet $entry.FullName "packet member name must equal '$expectedName'" }
        $artifact = $map[$pair.Artifact]
        $inputs.Add([pscustomobject]@{ File = $entry; Bytes = $bytes; Pair = $pair; Artifact = $artifact })
    }
    foreach ($artifact in $Compiled.Artifacts) {
        if (-not $seenNames.Contains("$($artifact.Id).md")) { Fail-Packet $lexicalDirectory "packet is missing required file '$($artifact.Id).md'" }
    }

    $payloads = Get-WduLifecycleRawExportBytes -CanonicalPath $CanonicalPath -Artifacts @($Compiled.Artifacts | ForEach-Object { $_.Id }) -RawExportCommand $RawExportCommand
    foreach ($input in $inputs) {
        $expected = Get-ExpectedBlockBytes -Artifact $input.Artifact.Id -LineEnding (Get-LineEndingBytes $input.Bytes) -Payload $payloads[$input.Artifact.Id]
        if ($RequireExact) {
            $actual = $input.Bytes[$input.Pair.Start..($input.Pair.End - 1)]
            if ([Convert]::ToHexString($actual) -cne [Convert]::ToHexString($expected)) {
                Fail-Packet $input.File.FullName "artifact '$($input.Pair.Artifact)' does not equal the exact raw projection block"
            }
        }
        $input | Add-Member -NotePropertyName Expected -NotePropertyValue $expected
    }

    [pscustomobject]@{ Inputs = @($inputs); LexicalDirectory = $lexicalDirectory; PhysicalDirectory = $physicalDirectory }
}

function Test-WduLifecyclePacket {
    param(
        [Parameter(Mandatory)][string] $CanonicalPath,
        [Parameter(Mandatory)][string] $PacketDirectory,
        [string] $RawExportCommand = $script:RawExportCommand
    )

    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $packet = Get-PacketInputs -Compiled $compiled -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory -RawExportCommand $RawExportCommand -RequireExact
    foreach ($artifact in $compiled.Artifacts) {
        $input = @($packet.Inputs | Where-Object { [StringComparer]::Ordinal.Equals($_.Artifact.Id, $artifact.Id) })[0]
        [pscustomobject]@{ Artifact = $artifact.Id; Path = $input.File.FullName; Result = 'ExactMatch' }
    }
}

function Export-WduLifecyclePacket {
    param(
        [Parameter(Mandatory)][string] $CanonicalPath,
        [Parameter(Mandatory)][string] $PacketDirectory,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [string] $RawExportCommand = $script:RawExportCommand
    )

    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $packet = Get-PacketInputs -Compiled $compiled -CanonicalPath $CanonicalPath -PacketDirectory $PacketDirectory -RawExportCommand $RawExportCommand
    $destinationLexical = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $destinationLexical) { Fail-Packet $destinationLexical 'render destination already exists' }
    $parent = [IO.Directory]::GetParent($destinationLexical)
    if ($null -eq $parent -or -not $parent.Exists) { Fail-Packet $destinationLexical 'render destination parent does not exist' }
    $physicalParent = Resolve-ExistingDirectory $parent.FullName $destinationLexical
    $leaf = [IO.Path]::GetFileName($destinationLexical)
    if ([string]::IsNullOrEmpty($leaf)) { Fail-Packet $destinationLexical 'render destination must name a new leaf directory' }
    $destinationPhysical = Join-Path $physicalParent $leaf
    if ((Test-PathWithin $destinationLexical $packet.LexicalDirectory) -or (Test-PathWithin $destinationPhysical $packet.PhysicalDirectory)) {
        Fail-Packet $destinationLexical 'render destination aliases an input artifact directory'
    }

    $staging = Join-Path $physicalParent ('.' + $leaf + '.staging-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        $writes = 0
        foreach ($input in $packet.Inputs) {
            [byte[]] $prefix = [byte[]]::new(0)
            if ($input.Pair.Start -gt 0) { $prefix = $input.Bytes[0..($input.Pair.Start - 1)] }
            [byte[]] $suffix = [byte[]]::new(0)
            if ($input.Pair.End -lt $input.Bytes.Length) { $suffix = $input.Bytes[$input.Pair.End..($input.Bytes.Length - 1)] }
            $output = [byte[]]::new($prefix.Length + $input.Expected.Length + $suffix.Length)
            [Array]::Copy($prefix, 0, $output, 0, $prefix.Length)
            [Array]::Copy($input.Expected, 0, $output, $prefix.Length, $input.Expected.Length)
            [Array]::Copy($suffix, 0, $output, $prefix.Length + $input.Expected.Length, $suffix.Length)
            [IO.File]::WriteAllBytes((Join-Path $staging "$($input.Artifact.Id).md"), $output)
            $writes++
            if ($null -ne $script:FailureAfterStagedWritesForTest -and $writes -ge $script:FailureAfterStagedWritesForTest) {
                throw 'test-only deterministic failure after staged write'
            }
        }
        [IO.Directory]::Move($staging, $destinationPhysical)
    }
    catch {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        throw
    }

    @(Get-ChildItem -LiteralPath $destinationPhysical -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
}

Export-ModuleMember -Function Test-WduLifecyclePacket, Export-WduLifecyclePacket
