[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$validatorPath = Join-Path $repositoryRoot 'scripts/validate-wdu-lifecycle-contract.ps1'
$canonicalSource = Join-Path $repositoryRoot 'docs/Working Directory Update.md'
$adrSource = Join-Path $repositoryRoot 'docs/adr/0011-working-directory-update-transaction.md'
$projectionFixtureSource = Join-Path $PSScriptRoot 'fixtures/wdu-lifecycle-projections'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "grace-wdu-882-tests-$([Guid]::NewGuid().ToString('N'))"
$script:Passed = 0
$script:Failed = 0

function Invoke-Case([string] $name, [scriptblock] $body) {
    try {
        & $body
        $script:Passed++
        Write-Host "PASS $name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $name :: $($_.Exception.Message)"
    }
}

function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw "Assertion failed: $message" }
}

function New-Packet([string] $name, [ValidateSet('LF', 'CRLF')] [string] $lineEnding = 'LF') {
    $root = Join-Path $testRoot $name
    $issueRoot = Join-Path $root 'offline issue bodies'
    [void](New-Item -ItemType Directory -Path $issueRoot -Force)
    $canonical = Join-Path $root 'Working Directory Update.md'
    $adr = Join-Path $root '0011 transaction.md'
    Copy-Item -LiteralPath $canonicalSource -Destination $canonical
    Copy-Item -LiteralPath $adrSource -Destination $adr
    Get-ChildItem -LiteralPath $projectionFixtureSource -Filter '*.md' | Copy-Item -Destination $issueRoot
    if ($lineEnding -eq 'CRLF') {
        foreach ($path in @($canonical, $adr) + @(Get-ChildItem $issueRoot -File | Select-Object -ExpandProperty FullName)) {
            $text = [IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
            [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
        }
    }
    return [pscustomobject]@{ Root = $root; Canonical = $canonical; Adr = $adr; Issues = $issueRoot }
}

function Invoke-Validator($packet, [string] $renderOutputPath = '') {
    $issues = @(Get-ChildItem -LiteralPath $packet.Issues -Filter '*.md' | Sort-Object Name | Select-Object -ExpandProperty FullName)
    $parameters = @{
        CanonicalPath = $packet.Canonical
        ProjectionPath = @($packet.Adr)
        OfflineIssueBodyPath = $issues
    }
    if ($renderOutputPath) { $parameters.RenderOutputPath = $renderOutputPath }
    return @(& $validatorPath @parameters)
}

function Assert-ValidatorFails($packet, [string] $pattern) {
    try {
        $null = Invoke-Validator $packet
    } catch {
        if ($_.Exception.Message -notmatch $pattern) {
            throw "Expected failure '$pattern', received '$($_.Exception.Message)'"
        }
        return
    }
    throw "Expected validator failure '$pattern'."
}

function Replace-Text([string] $path, [string] $old, [string] $new) {
    $text = [IO.File]::ReadAllText($path)
    if (-not $text.Contains($old)) { throw "Fixture mutation source not found: $old" }
    [IO.File]::WriteAllText($path, $text.Replace($old, $new), [Text.UTF8Encoding]::new($false))
}

function Add-OutsideProjection([string] $path, [string] $text) {
    [IO.File]::AppendAllText($path, "`n$text`n", [Text.UTF8Encoding]::new($false))
}

function Get-FileDigest([string] $path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Get-OutsideProjection([string] $path) {
    $text = [IO.File]::ReadAllText($path)
    $start = $text.IndexOf('<!-- grace:wdu-lifecycle-projection:start -->', [StringComparison]::Ordinal)
    $endMarker = '<!-- grace:wdu-lifecycle-projection:end -->'
    $end = $text.IndexOf($endMarker, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -lt $start) { throw "Projection markers missing in $path" }
    return $text.Substring(0, $start) + $text.Substring($end + $endMarker.Length)
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)

    Invoke-Case 'positive packet with spaces, Unicode, and LF' {
        $packet = New-Packet 'valid packet Ω with spaces' 'LF'
        $output = @(Invoke-Validator $packet)
        Assert-True ($output[-1] -match 'rows=67 keys=254 projections=9 decisions=9 requirements=17') 'positive summary'
    }

    Invoke-Case 'positive packet with CRLF' {
        $packet = New-Packet 'valid-crlf' 'CRLF'
        $null = Invoke-Validator $packet
    }

    Invoke-Case 'repository projection validates without optional issue exports' {
        $packet = New-Packet 'repository-only'
        $null = & $validatorPath -CanonicalPath $packet.Canonical -ProjectionPath $packet.Adr
    }

    Invoke-Case 'optional issue exports validate without repository projection' {
        $packet = New-Packet 'issues-only'
        $issues = @(Get-ChildItem $packet.Issues -Filter '*.md' | Select-Object -ExpandProperty FullName)
        $null = & $validatorPath -CanonicalPath $packet.Canonical -OfflineIssueBodyPath $issues
    }

    Invoke-Case 'missing canonical row fails' {
        $packet = New-Packet 'missing-row'
        Replace-Text $packet.Canonical '"id":"WDU-LC-143"' '"removedId":"WDU-LC-143"'
        Assert-ValidatorFails $packet 'expected 67 rows|row ID|row is missing id'
    }

    Invoke-Case 'duplicate canonical row ID fails' {
        $packet = New-Packet 'duplicate-row'
        Replace-Text $packet.Canonical '"id":"WDU-LC-143"' '"id":"WDU-LC-142"'
        Assert-ValidatorFails $packet 'duplicate row.*WDU-LC-142'
    }

    Invoke-Case 'coordinated replacement cannot change the required row set' {
        $packet = New-Packet 'replaced-required-row'
        Replace-Text $packet.Canonical '"id":"WDU-LC-143"' '"id":"WDU-LC-999"'
        Assert-ValidatorFails $packet 'lifecycle row IDs is not the exact canonical member set'
    }

    Invoke-Case 'aggregate expansion drift fails independently of expanded counts' {
        $packet = New-Packet 'aggregate-drift'
        Replace-Text $packet.Canonical '"ownedOrNone": ["notApplicable", "missing", "exact"]' `
            '"ownedOrNone": ["notApplicable", "missing", "unsupported"]'
        Assert-ValidatorFails $packet "marker aggregate 'ownedOrNone' is not the exact canonical member set"
    }

    Invoke-Case 'unknown projected row fails' {
        $packet = New-Packet 'unknown-projection'
        $path = Join-Path $packet.Issues 'issue-843.md'
        Replace-Text $path '"WDU-LC-003"' '"WDU-LC-999"'
        Assert-ValidatorFails $packet 'unknown row.*WDU-LC-999'
    }

    Invoke-Case 'overlapping applicability fails' {
        $packet = New-Packet 'overlap'
        Replace-Text $packet.Canonical '"value":"failureBeforeFirstWorkingTreeMutation"' '"value":"cancelBeforeFirstWorkingTreeMutation"'
        Assert-ValidatorFails $packet 'overlapping applicability'
    }

    Invoke-Case 'unknown grammar kind fails' {
        $packet = New-Packet 'unknown-kind'
        Replace-Text $packet.Canonical '"kind":"one","value":"missingMarkerFreshAdmission"' '"kind":"mystery","value":"missingMarkerFreshAdmission"'
        Assert-ValidatorFails $packet 'unknown predicate kind.*mystery'
    }

    Invoke-Case 'malformed tagged shape fails' {
        $packet = New-Packet 'malformed-shape'
        Replace-Text $packet.Canonical '"kind":"one","value":"missingMarkerFreshAdmission"' '"kind":"one","value":"missingMarkerFreshAdmission","name":"any"'
        Assert-ValidatorFails $packet 'malformed predicate properties'
    }

    Invoke-Case 'PR 873 publication-before-cleanup regression fails' {
        $packet = New-Packet 'pr873-reversal'
        Add-OutsideProjection $packet.Adr 'Branch publication then marker cleanup completes the pending operation.'
        Assert-ValidatorFails $packet 'competing or reversed lifecycle ordering prose'
    }

    Invoke-Case 'reversed ADR wording fails' {
        $packet = New-Packet 'adr-reversed'
        Add-OutsideProjection $packet.Adr 'Publish the selected Branch before exact marker cleanup.'
        Assert-ValidatorFails $packet 'competing or reversed lifecycle ordering prose'
    }

    Invoke-Case '#842 missing terminal-first row fails' {
        $packet = New-Packet '842-missing-terminal-first'
        $path = Join-Path $packet.Issues 'issue-842.md'
        Replace-Text $path "    `"WDU-LC-120`",`n" ''
        Assert-ValidatorFails $packet 'required row subset drift'
    }

    Invoke-Case '17/9 prose does not hide missing ledger row' {
        $packet = New-Packet 'ledger-false-positive'
        Replace-Text $packet.Canonical '| REQ-017 | Current public and contributor documentation | #846 | Final audit validates row references and removes competing sequences. |' 'The packet has 17 requirements and nine decisions.'
        Assert-ValidatorFails $packet 'expected 17 unique requirements'
    }

    Invoke-Case 'unmatched projection marker fails' {
        $packet = New-Packet 'unmatched-marker'
        $path = Join-Path $packet.Issues 'issue-869.md'
        Replace-Text $path '<!-- grace:wdu-lifecycle-projection:end -->' '<!-- marker removed -->'
        Assert-ValidatorFails $packet 'expected exactly one matched'
    }

    Invoke-Case 'copied second normative table fails' {
        $packet = New-Packet 'second-table'
        Add-OutsideProjection $packet.Adr '```json{"schema":"grace.wdu.branch-lifecycle/v1"}```'
        Assert-ValidatorFails $packet 'second normative lifecycle table'
    }

    Invoke-Case 'fixed cleanup-or-publication wording fails' {
        $packet = New-Packet 'fixed-retry-cutoff'
        Add-OutsideProjection $packet.Adr 'Retry performs cleanup or publication.'
        Assert-ValidatorFails $packet 'competing or reversed lifecycle ordering prose'
    }

    Invoke-Case 'check-only leaves every input unchanged' {
        $packet = New-Packet 'check-only'
        $paths = @($packet.Canonical, $packet.Adr) + @(Get-ChildItem $packet.Issues -File | Select-Object -ExpandProperty FullName)
        $before = @{}; foreach ($path in $paths) { $before[$path] = Get-FileDigest $path }
        $null = Invoke-Validator $packet
        foreach ($path in $paths) { Assert-True ((Get-FileDigest $path) -eq $before[$path]) "check-only changed $path" }
    }

    Invoke-Case 'explicit render is deterministic and does not edit inputs' {
        $packet = New-Packet 'render'
        $paths = @($packet.Adr) + @(Get-ChildItem $packet.Issues -File | Select-Object -ExpandProperty FullName)
        $before = @{}; foreach ($path in $paths) { $before[$path] = Get-FileDigest $path }
        $first = Join-Path $packet.Root 'render one'
        $second = Join-Path $packet.Root 'render two'
        $null = Invoke-Validator $packet $first
        $null = Invoke-Validator $packet $second
        foreach ($path in $paths) { Assert-True ((Get-FileDigest $path) -eq $before[$path]) "render changed source $path" }
        $firstFiles = @(Get-ChildItem $first -File | Sort-Object Name)
        $secondFiles = @(Get-ChildItem $second -File | Sort-Object Name)
        Assert-True ($firstFiles.Count -eq 9 -and $secondFiles.Count -eq 9) 'rendered packet count'
        for ($index = 0; $index -lt $firstFiles.Count; $index++) {
            Assert-True ($firstFiles[$index].Name -eq $secondFiles[$index].Name) 'rendered names'
            Assert-True ((Get-FileDigest $firstFiles[$index].FullName) -eq (Get-FileDigest $secondFiles[$index].FullName)) 'deterministic render'
            $source = @($paths | Where-Object { [IO.Path]::GetFileName($_) -eq $firstFiles[$index].Name })
            Assert-True ($source.Count -eq 1) 'render source lookup'
            Assert-True ((Get-OutsideProjection $source[0]) -ceq (Get-OutsideProjection $firstFiles[$index].FullName)) `
                'render changed content outside markers'
        }
    }

    Invoke-Case 'explicit render repairs only a stale marker in output' {
        $packet = New-Packet 'render-stale-marker'
        $source = Join-Path $packet.Issues 'issue-843.md'
        Replace-Text $source '"WDU-LC-003"' '"WDU-LC-999"'
        $sourceBefore = Get-FileDigest $source
        $outputRoot = Join-Path $packet.Root 'render corrected'
        $null = Invoke-Validator $packet $outputRoot
        Assert-True ((Get-FileDigest $source) -eq $sourceBefore) 'stale source was edited'
        $renderedAdr = Join-Path $outputRoot ([IO.Path]::GetFileName($packet.Adr))
        $renderedIssues = @(Get-ChildItem $outputRoot -Filter '*.md' | Where-Object FullName -ne $renderedAdr |
                Select-Object -ExpandProperty FullName)
        $null = & $validatorPath -CanonicalPath $packet.Canonical -ProjectionPath $renderedAdr `
            -OfflineIssueBodyPath $renderedIssues
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "WDU lifecycle validator tests: passed=$script:Passed failed=$script:Failed"
if ($script:Failed -ne 0) { exit 1 }
