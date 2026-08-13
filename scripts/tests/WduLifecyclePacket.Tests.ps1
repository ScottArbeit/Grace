[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $root 'scripts/modules/WduLifecycleProjection.psm1') -Force
Import-Module (Join-Path $root 'scripts/modules/WduLifecycleContract.psm1') -Force

function Assert-True { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw "Assertion failed: $Message" } }
function Assert-Fails { param([scriptblock] $Action, [string] $Text) try { & $Action | Out-Null } catch { if ($_.Exception.Message.Contains($Text, [StringComparison]::Ordinal)) { return }; throw }; throw "Expected '$Text'" }
function Invoke-Case { param([string] $Name, [scriptblock] $Action) try { & $Action; $script:Passed++; Write-Host "PASS $Name" } catch { $script:Failed++; Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red } }
function Get-MarkerForTest { param([string] $Artifact, [string] $Kind) "<!-- grace:wdu-lifecycle-projection:$Artifact`:$Kind -->" }
function Get-Hashes { param([string[]] $Paths) @($Paths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) }
function Find-Bytes { param([byte[]] $Bytes, [byte[]] $Needle, [int] $Offset = 0) for ($index = $Offset; $index -le $Bytes.Length - $Needle.Length; $index++) { $match = $true; for ($needleIndex = 0; $needleIndex -lt $Needle.Length; $needleIndex++) { if ($Bytes[$index + $needleIndex] -ne $Needle[$needleIndex]) { $match = $false; break } }; if ($match) { return $index } }; -1 }
function Get-Span { param([byte[]] $Bytes, [string] $Artifact) $utf8 = [Text.UTF8Encoding]::new($false, $true); $startMarker = $utf8.GetBytes((Get-MarkerForTest $Artifact 'start')); $endMarker = $utf8.GetBytes((Get-MarkerForTest $Artifact 'end')); $start = Find-Bytes $Bytes $startMarker; $end = Find-Bytes $Bytes $endMarker ($start + $startMarker.Length); if ($start -lt 0 -or $end -lt 0) { throw "marker span not found for $Artifact" }; [pscustomobject]@{ Start = $start; End = $end + $endMarker.Length } }
function Assert-ByteEqual { param([byte[]] $Left, [byte[]] $Right, [string] $Message) Assert-True ([Convert]::ToHexString($Left) -ceq [Convert]::ToHexString($Right)) $Message }
function Copy-Packet { param([string] $Source, [string] $Name) $destination = Join-Path $testRoot $Name; Copy-Item -LiteralPath $Source -Destination $destination -Recurse; $destination }

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-936-' + [guid]::NewGuid().ToString('N'))
$module = $null
$script:Passed = 0
$script:Failed = 0
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $canonical = Join-Path $root 'docs/Working Directory Update.md'
    $compiled = Read-WduLifecycleContract -Path $canonical
    $seed = Join-Path $testRoot 'seed'
    [IO.Directory]::CreateDirectory($seed) | Out-Null
    foreach ($artifact in $compiled.Artifacts) {
        $line = if (($artifact.Id.GetHashCode() % 2) -eq 0) { "`r`n" } else { "`n" }
        $bom = if ($artifact.Id -eq 'adr-0011') { [byte[]](239, 187, 191) } else { [byte[]]::new(0) }
        $body = "prefix-$($artifact.Id)-é${line}$(Get-MarkerForTest $artifact.Id 'start')${line}```json${line}{}${line}```${line}$(Get-MarkerForTest $artifact.Id 'end')${line}suffix-$($artifact.Id)-é"
        [IO.File]::WriteAllBytes((Join-Path $seed "$($artifact.Id).md"), [byte[]]($bom + [Text.UTF8Encoding]::new($false, $true).GetBytes($body)))
    }

    $before = Get-Hashes @(Get-ChildItem -LiteralPath $seed -File | Select-Object -ExpandProperty FullName)
    $rendered = Join-Path $testRoot 'rendered'
    $renderedFiles = @(Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $rendered)
    Invoke-Case 'renders exactly 15 declared packet files' { Assert-True ($renderedFiles.Count -eq 15) 'render must produce 15 files' }
    Invoke-Case 'preserves every source input hash after render' { $after = Get-Hashes @(Get-ChildItem -LiteralPath $seed -File | Select-Object -ExpandProperty FullName); Assert-True (($after -join '|') -ceq ($before -join '|')) 'source hashes change' }
    Invoke-Case 'checks every rendered payload exactly' { Assert-True (@(Test-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $rendered).Count -eq 15) 'exact packet result count' }
    Invoke-Case 'rejects an existing destination before staging' { Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $rendered } 'render destination already exists' }

    foreach ($artifact in $compiled.Artifacts) {
        $artifactId = $artifact.Id
        Invoke-Case "preserves prefix bytes for $artifactId" {
            $source = [IO.File]::ReadAllBytes((Join-Path $seed "$artifactId.md")); $output = [IO.File]::ReadAllBytes((Join-Path $rendered "$artifactId.md")); $sourceSpan = Get-Span $source $artifactId; $outputSpan = Get-Span $output $artifactId
            Assert-ByteEqual $source[0..($sourceSpan.Start - 1)] $output[0..($outputSpan.Start - 1)] "$artifactId prefix"
        }
        Invoke-Case "preserves suffix bytes for $artifactId" {
            $source = [IO.File]::ReadAllBytes((Join-Path $seed "$artifactId.md")); $output = [IO.File]::ReadAllBytes((Join-Path $rendered "$artifactId.md")); $sourceSpan = Get-Span $source $artifactId; $outputSpan = Get-Span $output $artifactId
            Assert-ByteEqual $source[$sourceSpan.End..($source.Length - 1)] $output[$outputSpan.End..($output.Length - 1)] "$artifactId suffix"
        }
    }
    $checked = @(Test-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $rendered)
    foreach ($artifact in $compiled.Artifacts) {
        $artifactId = $artifact.Id
        Invoke-Case "proves exact payload for $artifactId" { Assert-True ((@($checked | Where-Object { $_.Artifact -ceq $artifactId -and $_.Result -ceq 'ExactMatch' }).Count -eq 1)) "$artifactId exact result" }
    }

    $mutationCases = @(
        @{ Name = 'changes'; Mutate = { param([byte[]] $bytes) $bytes[0] = 0x78; $bytes } },
        @{ Name = 'drops'; Mutate = { param([byte[]] $bytes) $bytes[1..($bytes.Length - 1)] } },
        @{ Name = 'adds'; Mutate = { param([byte[]] $bytes) [byte[]](0x78) + $bytes } },
        @{ Name = 'normalizes'; Mutate = { param([byte[]] $bytes) [Text.UTF8Encoding]::new($false).GetBytes(([Text.UTF8Encoding]::new($false).GetString($bytes).Replace("`r`n", "`n"))) } }
    )
    foreach ($case in $mutationCases) {
        Invoke-Case "preservation assertion rejects outside-marker byte $($case.Name)" {
            $artifactId = if ($case.Name -eq 'normalizes') { @($compiled.Artifacts | Where-Object { [IO.File]::ReadAllBytes((Join-Path $seed "$($_.Id).md")) -contains 13 })[0].Id } else { 'adr-0011' }
            $source = [IO.File]::ReadAllBytes((Join-Path $seed "$artifactId.md")); $output = [IO.File]::ReadAllBytes((Join-Path $rendered "$artifactId.md")); $span = Get-Span $output $artifactId; $mutated = & $case.Mutate $output
            Assert-True ([Convert]::ToHexString($source[0..($span.Start - 1)]) -cne [Convert]::ToHexString($mutated[0..($span.Start - 1)])) "outside-marker mutation '$($case.Name)' was not detected"
        }
    }

    $markerCases = @(
        @{ Name = 'missing marker evidence'; Mutate = { param($text) $text -replace '<!-- grace:wdu-lifecycle-projection:adr-0011:(start|end) -->', '' }; Reason = 'is missing lifecycle projection marker evidence' },
        @{ Name = 'missing start marker'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'start'), '') }; Reason = "is missing start marker for 'adr-0011'" },
        @{ Name = 'missing end marker'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'end'), '') }; Reason = "is missing end marker for 'adr-0011'" },
        @{ Name = 'duplicate start marker'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'start'), "$(Get-MarkerForTest 'adr-0011' 'start')`n$(Get-MarkerForTest 'adr-0011' 'start')") }; Reason = "contains duplicate start marker for 'adr-0011'" },
        @{ Name = 'duplicate end marker'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'end'), "$(Get-MarkerForTest 'adr-0011' 'end')`n$(Get-MarkerForTest 'adr-0011' 'end')") }; Reason = "contains duplicate end marker for 'adr-0011'" },
        @{ Name = 'reversed markers'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'start'), '<!-- temporary -->').Replace((Get-MarkerForTest 'adr-0011' 'end'), (Get-MarkerForTest 'adr-0011' 'start')).Replace('<!-- temporary -->', (Get-MarkerForTest 'adr-0011' 'end')) }; Reason = "contains reversed end marker for 'adr-0011'" },
        @{ Name = 'nested markers'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'end'), "$(Get-MarkerForTest 'issue-842' 'start')`n$(Get-MarkerForTest 'adr-0011' 'end')`n$(Get-MarkerForTest 'issue-842' 'end')") }; Reason = 'contains nested lifecycle projection markers' },
        @{ Name = 'mismatched markers'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'end'), (Get-MarkerForTest 'issue-842' 'end')) }; Reason = "contains mismatched marker pair 'adr-0011' and 'issue-842'" },
        @{ Name = 'unknown marker'; Mutate = { param($text) $text.Replace('adr-0011:start', 'unknown:start').Replace('adr-0011:end', 'unknown:end') }; Reason = "contains unknown artifact marker 'unknown'" },
        @{ Name = 'malformed marker'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'start'), '<!-- grace:wdu-lifecycle-projection:adr-0011:middle -->') }; Reason = 'contains malformed lifecycle projection marker' },
        @{ Name = 'generic legacy marker'; Mutate = { param($text) $text.Replace((Get-MarkerForTest 'adr-0011' 'start'), '<!-- grace:wdu-lifecycle-projection:start -->') }; Reason = 'contains generic legacy lifecycle projection marker' },
        @{ Name = 'case-changed marker'; Mutate = { param($text) $text.Replace('adr-0011:start', 'ADR-0011:start') }; Reason = 'contains case-changed lifecycle projection marker' }
    )
    foreach ($case in $markerCases) {
        Invoke-Case "classifies $($case.Name)" {
            $packet = Copy-Packet $seed ('marker-' + $case.Name.Replace(' ', '-')); $path = Join-Path $packet 'adr-0011.md'; [IO.File]::WriteAllText($path, (& $case.Mutate ([IO.File]::ReadAllText($path))), [Text.UTF8Encoding]::new($false)); Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $testRoot ('out-' + $case.Name.Replace(' ', '-'))) } $case.Reason
        }
    }

    foreach ($case in @(
        @{ Name = 'missing packet member'; Act = { param($packet) [IO.File]::Delete((Join-Path $packet 'adr-0011.md')) }; Reason = 'must contain exactly 15 packet members' },
        @{ Name = 'extra non-Markdown member'; Act = { param($packet) [IO.File]::WriteAllText((Join-Path $packet 'extra.txt'), 'unexpected', [Text.UTF8Encoding]::new($false)) }; Reason = 'must contain exactly 15 packet members' },
        @{ Name = 'renamed packet member'; Act = { param($packet) Move-Item (Join-Path $packet 'adr-0011.md') (Join-Path $packet 'renamed.md') }; Reason = "packet member name must equal 'adr-0011.md'" },
        @{ Name = 'case-changed packet member'; Act = { param($packet) Move-Item (Join-Path $packet 'adr-0011.md') (Join-Path $packet 'ADR-0011.md') }; Reason = "packet member name must equal 'adr-0011.md'" },
        @{ Name = 'physical-alias packet member'; Act = { param($packet) $target = Join-Path $packet 'issue-842.md'; [IO.File]::Delete($target); New-Item -ItemType SymbolicLink -Path $target -Target (Join-Path $packet 'adr-0011.md') | Out-Null }; Reason = 'packet member must not be a physical alias' }
    )) {
        Invoke-Case "rejects $($case.Name)" { $packet = Copy-Packet $seed ('membership-' + $case.Name.Replace(' ', '-')); & $case.Act $packet; Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $testRoot ('out-' + $case.Name.Replace(' ', '-'))) } $case.Reason }
    }

    Invoke-Case 'rejects a lexical destination alias before staging' { Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory (Join-Path $seed 'inside') } 'render destination aliases an input artifact directory' }
    Invoke-Case 'rejects a symbolic-link destination alias before staging' {
        $packet = Copy-Packet $seed 'symbolic-link-source'; $alias = Join-Path $testRoot 'seed-link'; New-Item -ItemType SymbolicLink -Path $alias -Target $packet | Out-Null
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $alias 'inside') } 'render destination aliases an input artifact directory'
    }
    Invoke-Case 'rejects a junction destination alias before staging' {
        $packet = Copy-Packet $seed 'junction-source'; $alias = Join-Path $testRoot 'seed-junction'; New-Item -ItemType Junction -Path $alias -Target $packet | Out-Null
        Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $packet -OutputDirectory (Join-Path $alias 'inside') } 'render destination aliases an input artifact directory'
    }

    Invoke-Case 'fails after staged writes without a final output or staging residue' {
        $module = Get-Module WduLifecycleProjection; & $module { $script:FailureAfterStagedWritesForTest = 2 }
        $failed = Join-Path $testRoot 'failed'; Assert-Fails { Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $failed } 'test-only deterministic failure'
        Assert-True (-not (Test-Path -LiteralPath $failed)) 'final output exists'; Assert-True (@(Get-ChildItem -LiteralPath $testRoot -Directory -Filter '*.staging-*').Count -eq 0) 'staging remains'
        & $module { $script:FailureAfterStagedWritesForTest = $null }
    }
    Invoke-Case 'renders deterministically with byte-identical complete packets' {
        $second = Join-Path $testRoot 'rendered-second'; Export-WduLifecycleProjection -CanonicalPath $canonical -PacketDirectory $seed -OutputDirectory $second | Out-Null
        foreach ($artifact in $compiled.Artifacts) { Assert-ByteEqual ([IO.File]::ReadAllBytes((Join-Path $rendered "$($artifact.Id).md"))) ([IO.File]::ReadAllBytes((Join-Path $second "$($artifact.Id).md"))) "$($artifact.Id) deterministic bytes" }
    }
}
finally {
    if ($null -ne $module) { & $module { $script:FailureAfterStagedWritesForTest = $null } }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
