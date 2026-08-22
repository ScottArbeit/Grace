[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$canonical = Join-Path $root 'docs/Working Directory Update.md'
$renderCommand = Join-Path $root 'scripts/render-wdu-lifecycle-packet.ps1'
$checkCommand = Join-Path $root 'scripts/check-wdu-lifecycle-packet.ps1'
$rawExportCommand = Join-Path $root 'scripts/get-wdu-lifecycle-projection.ps1'
$packetModule = Join-Path $root 'scripts/modules/WduLifecyclePacket.psm1'
$contractModule = Join-Path $root 'scripts/modules/WduLifecycleContract.psm1'

function Assert-True { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw "Assertion failed: $Message" } }
function Assert-Bytes { param([byte[]] $Actual, [byte[]] $Expected, [string] $Message) Assert-True ($Actual.Length -eq $Expected.Length) "$Message length"; Assert-True ([Convert]::ToHexString($Actual) -ceq [Convert]::ToHexString($Expected)) "$Message bytes" }
function Assert-Fails { param([scriptblock] $Action, [string] $Reason) try { & $Action | Out-Null } catch { if ($_.Exception.Message.Contains($Reason, [StringComparison]::Ordinal)) { return }; throw }; throw "Expected '$Reason'" }
function Invoke-Case { param([string] $Name, [scriptblock] $Action) try { & $Action; $script:Passed++; Write-Host "PASS $Name" } catch { $script:Failed++; Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red } }
function Get-Marker { param([string] $Artifact, [ValidateSet('start', 'end')][string] $Kind) "<!-- grace:wdu-lifecycle-projection:$Artifact`:$Kind -->" }
function Get-Hashes { param([string] $Directory) @((Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name | ForEach-Object { "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" })) }
function Find-Bytes { param([byte[]] $Bytes, [byte[]] $Needle, [int] $Offset = 0) for ($index = $Offset; $index -le $Bytes.Length - $Needle.Length; $index++) { $same = $true; for ($needleIndex = 0; $needleIndex -lt $Needle.Length; $needleIndex++) { if ($Bytes[$index + $needleIndex] -ne $Needle[$needleIndex]) { $same = $false; break } }; if ($same) { return $index } }; -1 }
function Get-Span { param([byte[]] $Bytes, [string] $Artifact) $utf8 = [Text.UTF8Encoding]::new($false, $true); $startMarker = $utf8.GetBytes((Get-Marker $Artifact start)); $endMarker = $utf8.GetBytes((Get-Marker $Artifact end)); $start = Find-Bytes $Bytes $startMarker; $end = Find-Bytes $Bytes $endMarker ($start + $startMarker.Length); Assert-True ($start -ge 0 -and $end -ge 0) "$Artifact marker span"; [pscustomobject]@{ Start = $start; End = $end + $endMarker.Length; StartMarkerLength = $startMarker.Length; EndMarkerStart = $end } }
function Copy-Packet { param([string] $Source, [string] $Name) $destination = Join-Path $testRoot $Name; Copy-Item -LiteralPath $Source -Destination $destination -Recurse; $destination }

function Invoke-WduExportProcess {
    param([string] $Artifact)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $rawExportCommand, '-CanonicalPath', $canonical, '-Artifact', $Artifact)) { [void] $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = [IO.MemoryStream]::new()
    try {
        [void] $process.Start()
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $null = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        Assert-True ($process.ExitCode -eq 0) "$Artifact export exit"
        Assert-True ([string]::IsNullOrEmpty($stderr)) "$Artifact export stderr"
        return ,$stdout.ToArray()
    }
    finally {
        $stdout.Dispose()
        $process.Dispose()
    }
}

function Get-PayloadFromBlock {
    param([byte[]] $Bytes, [string] $Artifact)

    $span = Get-Span $Bytes $Artifact
    [byte[]] $lineEnding = if ($Bytes[$span.Start + $span.StartMarkerLength] -eq 13) { @(13, 10) } else { @(10) }
    $payloadStart = $span.Start + $span.StartMarkerLength + $lineEnding.Length + 7 + $lineEnding.Length
    $payloadEnd = $span.EndMarkerStart - $lineEnding.Length - 3 - $lineEnding.Length
    Assert-True ($payloadEnd -ge $payloadStart) "$Artifact payload span"
    return ,$Bytes[$payloadStart..($payloadEnd - 1)]
}

Import-Module $packetModule -Force
Import-Module $contractModule -Force
$compiled = Read-WduLifecycleContract -Path $canonical
$artifacts = @($compiled.Artifacts | ForEach-Object { $_.Id })
$script:Passed = 0
$script:Failed = 0
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-948-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    Invoke-Case 'compiler declares exactly fifteen unique artifact IDs' {
        Assert-True ($artifacts.Count -eq 15) 'artifact count'
        Assert-True (@($artifacts | Select-Object -Unique).Count -eq 15) 'artifact uniqueness'
    }

    $seed = Join-Path $testRoot 'seed'
    [IO.Directory]::CreateDirectory($seed) | Out-Null
    foreach ($artifact in $artifacts) {
        $line = if (($artifacts.IndexOf($artifact) % 2) -eq 0) { "`r`n" } else { "`n" }
        $bom = if ($artifact -eq 'adr-0011') { [byte[]](239, 187, 191) } else { [byte[]]::new(0) }
        $body = "prefix-$artifact-é$line$(Get-Marker $artifact start)$line``````json$line{}$line``````$line$(Get-Marker $artifact end)$line" + "suffix-$artifact-é"
        [IO.File]::WriteAllBytes((Join-Path $seed "$artifact.md"), [byte[]]($bom + [Text.UTF8Encoding]::new($false, $true).GetBytes($body)))
    }

    $sourceHashes = Get-Hashes $seed
    $rendered = Join-Path $testRoot 'rendered'
    $renderedFiles = @(& $renderCommand -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $rendered)
    Invoke-Case 'render command produces exactly fifteen declared packet members' {
        Assert-True ($renderedFiles.Count -eq 15) 'rendered member count'
        Assert-True ((@(Get-ChildItem -LiteralPath $rendered -File | Sort-Object Name | Select-Object -ExpandProperty Name) -join '|') -ceq (($artifacts | ForEach-Object { "$_` .md".Replace(' ', '') } | Sort-Object) -join '|')) 'rendered names'
    }
    Invoke-Case 'render preserves every source hash and does not expose a partial output' {
        Assert-True (((Get-Hashes $seed) -join '|') -ceq ($sourceHashes -join '|')) 'source hashes'
        Assert-True (@(Get-ChildItem -LiteralPath $rendered -File).Count -eq 15) 'complete rendered output'
    }
    Invoke-Case 'check command proves exact membership without writing' {
        $before = Get-Hashes $rendered
        $result = @(& $checkCommand -CanonicalPath $canonical -PacketDirectory $rendered)
        Assert-True ($result.Count -eq 15) 'check count'
        Assert-True (@($result | Where-Object { $_.Result -ceq 'ExactMatch' }).Count -eq 15) 'exact check results'
        Assert-True (((Get-Hashes $rendered) -join '|') -ceq ($before -join '|')) 'check hashes'
    }

    foreach ($artifact in $artifacts) {
        Invoke-Case "compares $artifact inserted bytes to a fresh #947 process" {
            $source = [IO.File]::ReadAllBytes((Join-Path $seed "$artifact.md"))
            $output = [IO.File]::ReadAllBytes((Join-Path $rendered "$artifact.md"))
            $sourceSpan = Get-Span $source $artifact
            $outputSpan = Get-Span $output $artifact
            Assert-Bytes $output[0..($outputSpan.Start - 1)] $source[0..($sourceSpan.Start - 1)] "$artifact prefix"
            Assert-Bytes $output[$outputSpan.End..($output.Length - 1)] $source[$sourceSpan.End..($source.Length - 1)] "$artifact suffix"
            Assert-Bytes (Get-PayloadFromBlock $output $artifact) (Invoke-WduExportProcess $artifact) "$artifact fresh payload"
        }
    }

    foreach ($case in @(
            @{ Name = 'changes'; Mutate = { param([byte[]] $bytes) $copy = [byte[]]$bytes.Clone(); $copy[0] = 0x78; $copy } },
            @{ Name = 'drops'; Mutate = { param([byte[]] $bytes) $bytes[1..($bytes.Length - 1)] } },
            @{ Name = 'adds'; Mutate = { param([byte[]] $bytes) [byte[]](0x78) + $bytes } },
            @{ Name = 'normalizes'; Mutate = { param([byte[]] $bytes) [Text.UTF8Encoding]::new($false, $true).GetBytes(([Text.UTF8Encoding]::new($false, $true).GetString($bytes)).Replace("`r`n", "`n")) } }
        )) {
        Invoke-Case "detects outside-marker byte $($case.Name)" {
            $artifact = if ($case.Name -eq 'normalizes') { 'adr-0011' } else { 'epic-835' }
            $source = [IO.File]::ReadAllBytes((Join-Path $seed "$artifact.md"))
            $output = [IO.File]::ReadAllBytes((Join-Path $rendered "$artifact.md"))
            $sourceSpan = Get-Span $source $artifact
            $outputSpan = Get-Span $output $artifact
            $sourceOutside = $source[0..($sourceSpan.Start - 1)] + $source[$sourceSpan.End..($source.Length - 1)]
            $outputOutside = $output[0..($outputSpan.Start - 1)] + $output[$outputSpan.End..($output.Length - 1)]
            $mutated = & $case.Mutate $outputOutside
            Assert-True ([Convert]::ToHexString($sourceOutside) -ceq [Convert]::ToHexString($outputOutside)) "$artifact baseline outside bytes"
            Assert-True ([Convert]::ToHexString($sourceOutside) -cne [Convert]::ToHexString($mutated)) "$artifact $($case.Name) is detected"
        }
    }

    Invoke-Case 'rejects an existing output before staging' { Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $rendered } 'render destination already exists' }
    Invoke-Case 'renders deterministic byte-identical packets' {
        $second = Join-Path $testRoot 'second'
        & $renderCommand -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $second | Out-Null
        foreach ($artifact in $artifacts) { Assert-Bytes ([IO.File]::ReadAllBytes((Join-Path $rendered "$artifact.md"))) ([IO.File]::ReadAllBytes((Join-Path $second "$artifact.md"))) "$artifact deterministic" }
    }

    $markerCases = @(
        @{ Name = 'missing start'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' start), '') }; Reason = "is missing start marker for 'adr-0011'" },
        @{ Name = 'missing end'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' end), '') }; Reason = "is missing end marker for 'adr-0011'" },
        @{ Name = 'missing evidence'; Mutate = { param($text) $text -replace '<!-- grace:wdu-lifecycle-projection:adr-0011:(start|end) -->', '' }; Reason = 'is missing lifecycle projection marker evidence' },
        @{ Name = 'duplicate start'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' start), "$(Get-Marker 'adr-0011' start)`n$(Get-Marker 'adr-0011' start)") }; Reason = "contains duplicate start marker for 'adr-0011'" },
        @{ Name = 'duplicate end'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' end), "$(Get-Marker 'adr-0011' end)`n$(Get-Marker 'adr-0011' end)") }; Reason = "contains duplicate end marker for 'adr-0011'" },
        @{ Name = 'reversed'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' start), '<!-- temporary -->').Replace((Get-Marker 'adr-0011' end), (Get-Marker 'adr-0011' start)).Replace('<!-- temporary -->', (Get-Marker 'adr-0011' end)) }; Reason = "contains reversed end marker for 'adr-0011'" },
        @{ Name = 'nested'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' end), "$(Get-Marker 'issue-842' start)`n$(Get-Marker 'adr-0011' end)`n$(Get-Marker 'issue-842' end)") }; Reason = 'contains nested lifecycle projection markers' },
        @{ Name = 'mismatched'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' end), (Get-Marker 'issue-842' end)) }; Reason = "contains mismatched marker pair 'adr-0011' and 'issue-842'" },
        @{ Name = 'unknown'; Mutate = { param($text) $text.Replace('adr-0011', 'unknown') }; Reason = "contains unknown artifact marker 'unknown'" },
        @{ Name = 'generic'; Mutate = { param($text) $text.Replace((Get-Marker 'adr-0011' start), '<!-- grace:wdu-lifecycle-projection:start -->') }; Reason = 'contains generic legacy lifecycle projection marker' },
        @{ Name = 'case changed'; Mutate = { param($text) $text.Replace('adr-0011:start', 'ADR-0011:start') }; Reason = 'contains case-changed lifecycle projection marker' }
    )
    foreach ($case in $markerCases) {
        Invoke-Case "classifies $($case.Name) markers" {
            $packet = Copy-Packet $seed ('marker-' + $case.Name)
            $path = Join-Path $packet 'adr-0011.md'
            [IO.File]::WriteAllText($path, (& $case.Mutate ([IO.File]::ReadAllText($path))), [Text.UTF8Encoding]::new($false))
            Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $testRoot ('out-' + $case.Name)) } $case.Reason
        }
    }

    foreach ($case in @(
            @{ Name = 'extra opener whitespace'; Comment = '<!--  grace:wdu-lifecycle-projection:adr-0011:start -->'; Reason = 'contains malformed lifecycle projection marker' },
            @{ Name = 'missing opener whitespace'; Comment = '<!--grace:wdu-lifecycle-projection:adr-0011:start -->'; Reason = 'contains malformed lifecycle projection marker' },
            @{ Name = 'delimiter whitespace'; Comment = '<!-- grace:wdu-lifecycle-projection:adr-0011: start -->'; Reason = 'contains malformed lifecycle projection marker' },
            @{ Name = 'malformed kind'; Comment = '<!-- grace:wdu-lifecycle-projection:adr-0011:middle -->'; Reason = 'contains malformed lifecycle projection marker' },
            @{ Name = 'case sentinel'; Comment = '<!-- Grace:Wdu-Lifecycle-Projection:adr-0011:start -->'; Reason = 'contains case-changed lifecycle projection marker' },
            @{ Name = 'multiple comments'; Comment = '<!-- grace:wdu-lifecycle-projection:issue-842:start -->'; Reason = "is missing end marker for 'issue-842'" }
        )) {
        Invoke-Case "rejects valid pair plus $($case.Name)" {
            $packet = Copy-Packet $seed ('sentinel-' + $case.Name.Replace(' ', '-'))
            $path = Join-Path $packet 'adr-0011.md'
            [IO.File]::AppendAllText($path, "`n$($case.Comment)", [Text.UTF8Encoding]::new($false))
            Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $testRoot ('sentinel-out-' + $case.Name.Replace(' ', '-'))) } $case.Reason
        }
    }
    Invoke-Case 'accepts an unrelated ordinary comment' {
        $packet = Copy-Packet $seed 'ordinary-comment'
        [IO.File]::AppendAllText((Join-Path $packet 'adr-0011.md'), "`n<!-- ordinary human comment -->", [Text.UTF8Encoding]::new($false))
        & $renderCommand -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $testRoot 'ordinary-output') | Out-Null
    }

    foreach ($case in @(
            @{ Name = 'missing member'; Act = { param($packet) [IO.File]::Delete((Join-Path $packet 'adr-0011.md')) }; Reason = 'must contain exactly 15 packet members' },
            @{ Name = 'extra file'; Act = { param($packet) [IO.File]::WriteAllText((Join-Path $packet 'extra.txt'), 'unexpected') }; Reason = 'must contain exactly 15 packet members' },
            @{ Name = 'directory member'; Act = { param($packet) [IO.Directory]::CreateDirectory((Join-Path $packet 'extra')) | Out-Null }; Reason = 'must contain exactly 15 packet members' },
            @{ Name = 'renamed member'; Act = { param($packet) Move-Item -LiteralPath (Join-Path $packet 'adr-0011.md') -Destination (Join-Path $packet 'renamed.md') }; Reason = "packet member name must equal 'adr-0011.md'" },
            @{ Name = 'case member'; Act = { param($packet) Move-Item -LiteralPath (Join-Path $packet 'adr-0011.md') -Destination (Join-Path $packet 'ADR-0011.md') }; Reason = "packet member name must equal 'adr-0011.md'" },
            @{ Name = 'symbolic member'; Act = { param($packet) Remove-Item -LiteralPath (Join-Path $packet 'issue-842.md'); New-Item -ItemType SymbolicLink -Path (Join-Path $packet 'issue-842.md') -Target (Join-Path $packet 'adr-0011.md') | Out-Null }; Reason = 'packet member must not be a physical alias' }
        )) {
        Invoke-Case "rejects $($case.Name)" {
            $packet = Copy-Packet $seed ('member-' + $case.Name.Replace(' ', '-'))
            & $case.Act $packet
            Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $testRoot ('member-out-' + $case.Name.Replace(' ', '-'))) } $case.Reason
        }
    }

    Invoke-Case 'rejects lexical output aliases before staging' { Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory (Join-Path $seed 'inside') } 'render destination aliases an input artifact directory' }
    Invoke-Case 'rejects symbolic-link output aliases before staging' {
        $packet = Copy-Packet $seed 'symbolic-source'; $alias = Join-Path $testRoot 'symbolic-output-alias'; New-Item -ItemType SymbolicLink -Path $alias -Target $packet | Out-Null
        Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $alias 'inside') } 'render destination aliases an input artifact directory'
    }
    Invoke-Case 'rejects junction output aliases before staging' {
        $packet = Copy-Packet $seed 'junction-source'; $alias = Join-Path $testRoot 'junction-output-alias'; New-Item -ItemType Junction -Path $alias -Target $packet | Out-Null
        Assert-Fails { & $renderCommand -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $alias 'inside') } 'render destination aliases an input artifact directory'
    }
    Invoke-Case 'cleans staged writes after a deterministic late failure' {
        $module = Get-Module WduLifecyclePacket
        & $module { $script:FailureAfterStagedWritesForTest = 14 }
        $failed = Join-Path $testRoot 'failed'
        $before = Get-Hashes $seed
        try { Assert-Fails { Export-WduLifecyclePacket -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $failed } 'test-only deterministic failure' }
        finally { & $module { $script:FailureAfterStagedWritesForTest = $null } }
        Assert-True (((Get-Hashes $seed) -join '|') -ceq ($before -join '|')) 'late failure source hashes'
        Assert-True (-not (Test-Path -LiteralPath $failed)) 'late failure output'
        Assert-True (@(Get-ChildItem -LiteralPath $testRoot -Directory -Filter '.failed.staging-*').Count -eq 0) 'late failure residue'
    }
}
finally {
    $module = Get-Module WduLifecyclePacket
    if ($null -ne $module) { & $module { $script:FailureAfterStagedWritesForTest = $null } }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
