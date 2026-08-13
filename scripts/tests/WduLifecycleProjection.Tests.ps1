[CmdletBinding()]
param(
    [ValidateSet('All', 'PlanCore', 'PlanNegativeA', 'PlanNegativeB', 'PlanNegativeC', 'RequiredPacket', 'PlanMarkers', 'Marker', 'DriftA', 'DriftB', 'DriftC', 'PacketA', 'PacketAEmpty', 'PacketB', 'PacketBReadOnly', 'PacketBCommands')]
    [string] $Group = 'All'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$modulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleProjection.psm1'
$canonicalSource = Join-Path $repositoryRoot 'docs/Working Directory Update.md'
$getCommand = Join-Path $repositoryRoot 'scripts/get-wdu-lifecycle-projection.ps1'
$checkCommand = Join-Path $repositoryRoot 'scripts/check-wdu-lifecycle-projections.ps1'
$artifactIds = @('adr-0011', 'epic-835', 'issue-842', 'issue-843', 'issue-846', 'issue-869', 'issue-898', 'issue-920', 'issue-921', 'issue-922', 'issue-923', 'issue-900', 'issue-901', 'issue-871', 'issue-872')

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
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Write-TestText {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-TestText {
    param([string] $Path)
    return [IO.File]::ReadAllText($Path)
}

function Replace-Once {
    param([string] $Text, [string] $Old, [string] $New)
    $index = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Test anchor not found: $Old" }
    return $Text.Remove($index, $Old.Length).Insert($index, $New)
}

function New-Packet {
    param([string] $Name, [switch] $CrLf)
    $root = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $canonical = Join-Path $root 'canonical lifecycle.md'
    [IO.File]::Copy($canonicalSource, $canonical, $true)
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($artifact in $artifactIds) {
        $path = Join-Path $root "$artifact body.md"
        $outside = "# arbitrary $artifact é`n`n" + '<div data-lifecycle="not interpreted">outside</div>' + "`n`n" + '```json' + "`n" + '[false, 42, {"artifact":"outside"}]' + "`n" + '```' + "`n`n" + '```text' + "`ncontradictory prose is ignored`n" + '```' + "`n"
        $text = "$outside`n$(New-WduLifecycleProjection -CanonicalPath $canonical -Artifact $artifact)`n`n$outside"
        if ($CrLf) { $text = $text.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n") }
        Write-TestText $path $text
        $paths.Add($path)
    }
    return [pscustomobject]@{ Root = $root; Canonical = $canonical; Paths = @($paths) }
}

function Get-Hashes {
    param([string[]] $Paths)
    return @($Paths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
}

Import-Module $modulePath -Force
$script:Passed = 0
$script:Failed = 0
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-lifecycle-projection-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null

try {
    if ($Group -in @('All', 'PlanCore')) {
    Invoke-Case 'exports exactly the two projection functions' {
        $exports = @(Get-Command -Module WduLifecycleProjection | Select-Object -ExpandProperty Name | Sort-Object)
        Assert-True (($exports -join '|') -ceq ('New-WduLifecycleProjection|Test-WduLifecycleProjection')) 'module exports only the two public functions'
    }

    Invoke-Case 'generates one deterministic ADR projection from the compiled canonical plan' {
        $first = New-WduLifecycleProjection -CanonicalPath $canonicalSource -Artifact 'adr-0011'
        $second = New-WduLifecycleProjection -CanonicalPath $canonicalSource -Artifact 'adr-0011'
        Assert-True ($first -ceq $second) 'repeated in-process generation is byte-identical'
        Assert-True $first.StartsWith('<!-- grace:wdu-lifecycle-projection:start -->', [StringComparison]::Ordinal) 'generated block starts with its exact marker'
        Assert-True $first.EndsWith('<!-- grace:wdu-lifecycle-projection:end -->', [StringComparison]::Ordinal) 'generated block ends with its exact marker'
        Assert-True $first.Contains('"artifact": "adr-0011"', [StringComparison]::Ordinal) 'generated block carries its artifact ID'
    }

    Invoke-Case 'checks a complete packet with paths containing spaces and Unicode in arbitrary input order' {
        $packet = New-Packet 'paths with spaces é'
        $result = @(Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath @($packet.Paths[14..0]))
        Assert-True ($result.Count -eq 15) 'complete packet returns one scoped result per assignment'
        Assert-True (($result | Select-Object -ExpandProperty Artifact) -join '|' -ceq ($artifactIds -join '|')) 'results are in canonical assignment order'
    }

    Invoke-Case 'normalizes LF and CRLF only while checking exact blocks' {
        $packet = New-Packet 'crlf packet' -CrLf
        $result = @(Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths)
        Assert-True ($result.Count -eq 15) 'CRLF packet succeeds under the documented normalization'
    }

    }

    if ($Group -in @('All', 'PlanNegativeA', 'PlanNegativeB', 'PlanNegativeC')) {
    $planNegativeCases = @(
            @{ Name = 'duplicate artifact assignment'; Old = '"artifact":"issue-843"'; New = '"artifact":"issue-842"'; Reason = 'duplicate artifact' },
            @{ Name = 'duplicate assignment row ID'; Old = '"rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]'; New = '"rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-100","WDU-LC-103"]'; Reason = 'duplicate row ID' },
            @{ Name = 'unknown assignment row ID'; Old = '"WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-037"'; New = '"WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-999"'; Reason = 'unknown row ID' },
            @{ Name = 'blank assignment proof'; Old = '"proof":"Consume the completed Branch lifecycle while deferring the first real Watch producer and retry proof to this issue."'; New = '"proof":""'; Reason = 'must be a nonblank' },
            @{ Name = 'malformed assignment digest'; Old = 'aca9d1bb256f0ce7e9c93c2ed6ec9afd263faaa261632ed254dd7d28acc097e5'; New = '901fe35f11362c73d7200151e84ee2827dfee965758cb8e42925167c26aee7'; Reason = 'lowercase SHA-256' },
            @{ Name = 'unknown assignment property'; Old = '"schema": "grace.wdu.lifecycle-projection-plan/v1",'; New = '"schema": "grace.wdu.lifecycle-projection-plan/v1", "extra": true,'; Reason = 'unknown property' },
            @{ Name = 'incomplete assignment row coverage'; Old = ',"WDU-LC-037","WDU-LC-038","WDU-LC-100"'; New = ',"WDU-LC-038","WDU-LC-100"'; Reason = 'does not cover canonical row ID' }
        )
    $planNegativeSelected = if ($Group -eq 'PlanNegativeA') { @($planNegativeCases[0..2]) }
    elseif ($Group -eq 'PlanNegativeB') { @($planNegativeCases[3..5]) }
    elseif ($Group -eq 'PlanNegativeC') { @($planNegativeCases[6]) }
    else { $planNegativeCases }
    foreach ($case in $planNegativeSelected) {
        Invoke-Case "rejects $($case.Name)" {
            $packet = New-Packet ($case.Name.Replace(' ', '-'))
            Write-TestText $packet.Canonical (Replace-Once (Get-TestText $packet.Canonical) $case.Old $case.New)
            Assert-Fails { New-WduLifecycleProjection -CanonicalPath $packet.Canonical -Artifact 'adr-0011' } $case.Reason
        }
    }

    }

    if ($Group -in @('All', 'RequiredPacket')) {
    Invoke-Case 'rejects a renamed required assignment artifact' {
        $packet = New-Packet 'renamed-required-assignment-artifact'
        Write-TestText $packet.Canonical (Replace-Once (Get-TestText $packet.Canonical) '"artifact":"issue-872"' '"artifact":"issue-999"')
        Assert-Fails { New-WduLifecycleProjection -CanonicalPath $packet.Canonical -Artifact 'adr-0011' } "required artifact 'issue-872'"
    }

    Invoke-Case 'rejects reordered required assignment artifacts' {
        $packet = New-Packet 'reordered-required-assignment-artifacts'
        $reordered = Replace-Once (Get-TestText $packet.Canonical) '"artifact":"issue-901"' '"artifact":"temporary-ordering-artifact"'
        $reordered = Replace-Once $reordered '"artifact":"issue-871"' '"artifact":"issue-901"'
        $reordered = Replace-Once $reordered '"artifact":"temporary-ordering-artifact"' '"artifact":"issue-871"'
        Write-TestText $packet.Canonical $reordered
        Assert-Fails { New-WduLifecycleProjection -CanonicalPath $packet.Canonical -Artifact 'adr-0011' } "required artifact 'issue-901'"
    }

    Invoke-Case 'rejects closed issue-870 as a replacement packet member' {
        $packet = New-Packet 'closed-issue-870-member'
        Write-TestText $packet.Canonical (Replace-Once (Get-TestText $packet.Canonical) '"artifact":"issue-901"' '"artifact":"issue-870"')
        Assert-Fails { New-WduLifecycleProjection -CanonicalPath $packet.Canonical -Artifact 'adr-0011' } "required artifact 'issue-901'"
    }

    Invoke-Case 'requires the declared non-lifecycle exclusion for issue-897' {
        $packet = New-Packet 'issue-897-non-lifecycle-exclusion'
        Write-TestText $packet.Canonical (Replace-Once (Get-TestText $packet.Canonical) 'checker exports lifecycle consumers only.' 'changed reason.')
        Assert-Fails { New-WduLifecycleProjection -CanonicalPath $packet.Canonical -Artifact 'adr-0011' } "stale reason for excluded artifact 'issue-897'"
    }
    }

    if ($Group -in @('All', 'PlanMarkers')) {
    foreach ($case in @(
            @{ Name = 'missing assignment plan marker'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-projection-plan:start -->', '') }; Reason = 'marker pair' },
            @{ Name = 'duplicate assignment plan marker'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-projection-plan:start -->', "<!-- grace:wdu-lifecycle-projection-plan:start -->`n<!-- grace:wdu-lifecycle-projection-plan:start -->") }; Reason = 'marker pair' },
            @{ Name = 'reversed assignment plan markers'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-projection-plan:start -->', '<!-- temporary marker -->').Replace('<!-- grace:wdu-lifecycle-projection-plan:end -->', '<!-- grace:wdu-lifecycle-projection-plan:start -->').Replace('<!-- temporary marker -->', '<!-- grace:wdu-lifecycle-projection-plan:end -->') }; Reason = 'markers are reversed' }
        )) {
        Invoke-Case "rejects $($case.Name)" {
            $packet = New-Packet ($case.Name.Replace(' ', '-'))
            Write-TestText $packet.Canonical (& $case.Mutate (Get-TestText $packet.Canonical))
            Assert-Fails { New-WduLifecycleProjection -CanonicalPath $packet.Canonical -Artifact 'adr-0011' } $case.Reason
        }
    }
    }

    if ($Group -in @('All', 'Marker')) {
    foreach ($case in @(
            @{ Name = 'missing projection marker'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-projection:start -->', '') }; Reason = 'marker pair' },
            @{ Name = 'duplicate projection marker'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-projection:start -->', "<!-- grace:wdu-lifecycle-projection:start -->`n<!-- grace:wdu-lifecycle-projection:start -->") }; Reason = 'marker pair' },
            @{ Name = 'reversed projection markers'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-projection:start -->', '<!-- temporary marker -->').Replace('<!-- grace:wdu-lifecycle-projection:end -->', '<!-- grace:wdu-lifecycle-projection:start -->').Replace('<!-- temporary marker -->', '<!-- grace:wdu-lifecycle-projection:end -->') }; Reason = 'markers are reversed' }
        )) {
        Invoke-Case "rejects $($case.Name)" {
            $packet = New-Packet ($case.Name.Replace(' ', '-'))
            Write-TestText $packet.Paths[0] (& $case.Mutate (Get-TestText $packet.Paths[0]))
            Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths } $case.Reason
        }
    }
    }

    if ($Group -in @('All', 'DriftA', 'DriftB', 'DriftC')) {
    $driftCases = @(
            @{ Name = 'stale projection digest'; Old = 'aca9d1bb256f0ce7e9c93c2ed6ec9afd263faaa261632ed254dd7d28acc097e5'; New = '001fe35f11362c73d7200151e84ee2827dfee965758cb8e42925167c26aee7f7'; Reason = 'does not equal' },
            @{ Name = 'wrong projection artifact'; Old = '"artifact": "issue-842"'; New = '"artifact": "unexpected"'; Reason = 'unexpected artifact' },
            @{ Name = 'wrong projection anchor'; Old = 'docs/Working Directory Update.md#normative-branch-lifecycle-table'; New = 'docs/Working Directory Update.md#wrong-anchor'; Reason = 'does not equal' },
            @{ Name = 'changed projection proof'; Old = 'Prove Branch-only Doctor retry, refusal, cancellation boundaries, terminal replay, and no working-file mutation.'; New = 'changed proof'; Reason = 'does not equal' },
            @{ Name = 'missing projection row'; Old = "    `"WDU-LC-100`",`r`n"; New = ''; Reason = 'does not equal' },
            @{ Name = 'extra projection row'; Old = "    `"WDU-LC-100`",`r`n"; New = "    `"WDU-LC-extra`",`r`n    `"WDU-LC-100`",`r`n"; Reason = 'does not equal' },
            @{ Name = 'reordered projection row'; Old = "`"WDU-LC-100`",`r`n    `"WDU-LC-101`""; New = "`"WDU-LC-101`",`r`n    `"WDU-LC-100`""; Reason = 'does not equal' },
            @{ Name = 'extra projection property'; Old = '"schema": "grace.wdu.lifecycle-projection/v1",'; New = '"schema": "grace.wdu.lifecycle-projection/v1", "extra": true,'; Reason = 'does not equal' }
        )
    $driftSelected = if ($Group -eq 'DriftA') { @($driftCases[0..2]) }
    elseif ($Group -eq 'DriftB') { @($driftCases[3..5]) }
    elseif ($Group -eq 'DriftC') { @($driftCases[6..7]) }
    else { $driftCases }
    foreach ($case in $driftSelected) {
        Invoke-Case "rejects $($case.Name)" {
            $packet = New-Packet ($case.Name.Replace(' ', '-'))
            $target = $packet.Paths[2]
            Write-TestText $target (Replace-Once (Get-TestText $target) $case.Old $case.New)
            Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths } $case.Reason
        }
    }

    }

    if ($Group -in @('All', 'PacketA')) {

    Invoke-Case 'requires complete packet membership before success' {
        $packet = New-Packet 'missing packet artifact'
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths[0..7] } 'packet is missing required artifact'
    }

    Invoke-Case 'rejects a duplicate packet artifact before success' {
        $packet = New-Packet 'duplicate packet artifact'
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath @($packet.Paths + $packet.Paths[0]) } 'duplicate artifact'
    }

    Invoke-Case 'rejects an unexpected packet artifact before success' {
        $packet = New-Packet 'unexpected packet artifact'
        $unexpected = Join-Path $packet.Root 'unexpected.md'
        Write-TestText $unexpected (Replace-Once (Get-TestText $packet.Paths[2]) '"artifact": "issue-842"' '"artifact": "unexpected"')
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath @($packet.Paths + $unexpected) } 'unexpected artifact'
    }

    }

    if ($Group -in @('All', 'PacketAEmpty')) {
    Invoke-Case 'accepts empty content outside the exact projection markers' {
        $packet = New-Packet 'empty outside content'
        foreach ($path in $packet.Paths) {
            $text = Get-TestText $path
            $start = $text.IndexOf('<!-- grace:wdu-lifecycle-projection:start -->', [StringComparison]::Ordinal)
            $end = $text.IndexOf('<!-- grace:wdu-lifecycle-projection:end -->', [StringComparison]::Ordinal)
            $block = $text.Substring($start, ($end + '<!-- grace:wdu-lifecycle-projection:end -->'.Length) - $start)
            Write-TestText $path $block
        }
        $result = @(Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths)
        Assert-True ($result.Count -eq 15) 'empty surrounding content is ignored'
    }

    }

    if ($Group -in @('All', 'PacketB', 'PacketBReadOnly')) {

    Invoke-Case 'leaves every input byte unchanged on success and failure' {
        $packet = New-Packet 'read only packet'
        $all = @($packet.Canonical) + $packet.Paths
        $before = Get-Hashes $all
        Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths | Out-Null
        Assert-True ((Get-Hashes $all) -join '|' -ceq ($before -join '|')) 'successful check is read-only'
        Write-TestText $packet.Paths[0] ((Get-TestText $packet.Paths[0]).Replace('<!-- grace:wdu-lifecycle-projection:end -->', ''))
        $beforeFailure = Get-Hashes $all
        Assert-Fails { Test-WduLifecycleProjection -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths } 'marker pair'
        Assert-True ((Get-Hashes $all) -join '|' -ceq ($beforeFailure -join '|')) 'failed check is read-only'
    }

    Invoke-Case 'has no output-path parameter on the module or thin commands' {
        foreach ($command in @('New-WduLifecycleProjection', 'Test-WduLifecycleProjection', $getCommand, $checkCommand)) {
            $parameters = if ($command -like '*.ps1') { (Get-Command $command).Parameters.Keys } else { (Get-Command $command).Parameters.Keys }
            Assert-True (-not ($parameters | Where-Object { $_ -match 'output|render|stage|publish' })) "$command has no output or publication parameter"
        }
    }

    }

    if ($Group -in @('All', 'PacketB', 'PacketBCommands')) {
    Invoke-Case 'thin commands generate byte-identical stdout and check a complete offline packet' {
        $packet = New-Packet 'thin commands packet'
        $first = Join-Path $packet.Root 'first stdout.bin'
        $second = Join-Path $packet.Root 'second stdout.bin'
        & pwsh -NoProfile -File $getCommand -CanonicalPath $packet.Canonical -Artifact 'adr-0011' > $first
        Assert-True ($LASTEXITCODE -eq 0) 'first generator invocation succeeds'
        & pwsh -NoProfile -File $getCommand -CanonicalPath $packet.Canonical -Artifact 'adr-0011' > $second
        Assert-True ($LASTEXITCODE -eq 0) 'second generator invocation succeeds'
        Assert-True ([Convert]::ToHexString([IO.File]::ReadAllBytes($first)) -ceq [Convert]::ToHexString([IO.File]::ReadAllBytes($second))) 'stdout generation is byte-identical'
        $checked = @(& $checkCommand -CanonicalPath $packet.Canonical -ArtifactPath $packet.Paths)
        Assert-True ($checked.Count -eq 15) 'thin checker returns the fifteen scoped results'
    }
    }
}
finally {
    if (Test-Path -LiteralPath $script:TestRoot) { Remove-Item -LiteralPath $script:TestRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
