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
        Assert-ValidatorFails $packet 'expected 67 rows|row ID|row is missing id|unknown row property'
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
        Assert-ValidatorFails $packet 'malformed predicate properties|malformed JSON properties'
    }

    Invoke-Case 'PR 873 publication-before-cleanup regression fails' {
        $packet = New-Packet 'pr873-reversal'
        Add-OutsideProjection $packet.Adr 'Branch publication then marker cleanup completes the pending operation.'
        Assert-ValidatorFails $packet 'competing lifecycle source outside its projection'
    }

    Invoke-Case 'reversed ADR wording fails' {
        $packet = New-Packet 'adr-reversed'
        Add-OutsideProjection $packet.Adr 'Publish the selected Branch before exact marker cleanup.'
        Assert-ValidatorFails $packet 'competing lifecycle source outside its projection'
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

    Invoke-Case 'historical supersession sentence remains valid evidence' {
        $packet = New-Packet 'fixed-retry-cutoff'
        Add-OutsideProjection $packet.Adr @'
<!-- grace:wdu-lifecycle-historical-evidence:start -->
Historical supersession reference: [PR #873](https://github.com/ScottArbeit/Grace/pull/873).
<!-- grace:wdu-lifecycle-historical-evidence:end -->
'@
        $null = Invoke-Validator $packet
    }

    Invoke-Case 'copied canonical lifecycle sequence outside projection fails' {
        $packet = New-Packet 'copied-sequence'
        Add-OutsideProjection $packet.Adr 'The lifecycle will clean the marker, publish the Branch, prove publication, and record terminal completion.'
        Assert-ValidatorFails $packet 'competing lifecycle source outside its projection'
    }

    Invoke-Case 'terminal replay outcome mutation fails' {
        $packet = New-Packet 'terminal-replay-outcome'
        Replace-Text $packet.Canonical '"durableResult":"existingTerminal","outcome":"Unchanged"' `
            '"durableResult":"existingTerminal","outcome":"Updated"'
        Assert-ValidatorFails $packet "outcome 'Updated' requires durableResult 'terminal'|terminal replay must be unchanged"
    }

    Invoke-Case 'marker refusal cannot attempt publication' {
        $packet = New-Packet 'refusal-publication'
        Replace-Text $packet.Canonical '"requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged"' `
            '"requiredActions":["attemptPublishSelectedBranch"],"workingFiles":"unchanged"'
        Assert-ValidatorFails $packet 'disallowed marker cell cannot|Branch publication is only legal'
    }

    Invoke-Case 'unknown durable action fails with row diagnostic' {
        $packet = New-Packet 'unknown-durable-action'
        Replace-Text $packet.Canonical '"requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged"' `
            '"requiredActions":["unknownDurableAction"],"workingFiles":"unchanged"'
        Assert-ValidatorFails $packet "WDU-LC-202.*unknown durable action 'unknownDurableAction'"
    }

    Invoke-Case 'missing or malformed projection digest fails' {
        $packet = New-Packet 'malformed-projection-digest'
        $path = Join-Path $packet.Issues 'issue-843.md'
        Replace-Text $path '"canonicalContentDigest": "901fe35f11362c73d7200151e84ee2827dfee965758cb8e42925167c26aee7f7"' `
            '"canonicalContentDigest": "not-a-digest"'
        Assert-ValidatorFails $packet 'projection digest is missing or malformed'
    }

    Invoke-Case 'one-byte canonical lifecycle mutation rejects all projections' {
        $packet = New-Packet 'canonical-digest-mutation'
        Replace-Text $packet.Canonical '  "schema": "grace.wdu.branch-lifecycle/v1"' `
            '   "schema": "grace.wdu.branch-lifecycle/v1"'
        Assert-ValidatorFails $packet 'projection plan digest does not match canonical lifecycle content'
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

    Invoke-Case 'render preflight rejects duplicate basenames without partial output' {
        $packet = New-Packet 'render-duplicate-basename'
        $left = Join-Path $packet.Root 'left'; $right = Join-Path $packet.Root 'right'
        [void](New-Item -ItemType Directory -Path $left, $right)
        $leftSource = Join-Path $left 'same.md'; $rightSource = Join-Path $right 'same.md'
        Copy-Item -LiteralPath (Join-Path $packet.Issues 'issue-842.md') -Destination $leftSource
        Copy-Item -LiteralPath (Join-Path $packet.Issues 'issue-843.md') -Destination $rightSource
        $output = Join-Path $packet.Root 'no partial render'
        $remaining = @(Get-ChildItem -LiteralPath $packet.Issues -Filter '*.md' |
                Where-Object { $_.Name -notin @('issue-842.md', 'issue-843.md') } |
                Select-Object -ExpandProperty FullName)
        try {
            & $validatorPath -CanonicalPath $packet.Canonical -OfflineIssueBodyPath (@($leftSource, $rightSource) + $remaining) -RenderOutputPath $output
            throw 'Expected duplicate destination failure.'
        } catch {
            Assert-True ($_.Exception.Message -match 'duplicate render destination') "duplicate basename diagnostic: $($_.Exception.Message)"
        }
        Assert-True (-not (Test-Path -LiteralPath $output)) 'preflight created partial output directory'
    }

    Invoke-Case 'render preflight rejects every output-input intersection before bytes change' {
        $packet = New-Packet 'render-input-intersection'
        $before = Get-FileDigest $packet.Adr
        try {
            & $validatorPath -CanonicalPath $packet.Canonical -ProjectionPath $packet.Adr -RenderOutputPath $packet.Adr
            throw 'Expected input intersection failure.'
        } catch {
            Assert-True ($_.Exception.Message -match 'render output directory must not already exist|render output must not overwrite an input artifact') 'input intersection diagnostic'
        }
        Assert-True ((Get-FileDigest $packet.Adr) -eq $before) 'input bytes changed before preflight failure'
    }

    Invoke-Case 'closed semantic relation rejects coordinated publication mutation on WDU-LC-100' {
        $packet = New-Packet 'semantic-publication-mutation'
        Replace-Text $packet.Canonical '"requiredActions":["retainEvidence","retainPending"],"workingFiles":"unchanged"' `
            '"requiredActions":["attemptPublishSelectedBranch","retainEvidence","retainPending"],"workingFiles":"unchanged"'
        Assert-ValidatorFails $packet 'disallowed marker cell cannot|Branch publication is only legal'
    }

    Invoke-Case 'closed semantic relation rejects terminal recording before proof' {
        $packet = New-Packet 'semantic-terminal-before-proof'
        Replace-Text $packet.Canonical '"requiredActions":["proveSelectedBranch","recordTerminal"]' `
            '"requiredActions":["recordTerminal","proveSelectedBranch"]'
        Assert-ValidatorFails $packet 'terminal recording requires prior durable identity proof'
    }

    Invoke-Case 'unknown canonical root and nested projection fields fail' {
        $packet = New-Packet 'unknown-fields'
        Replace-Text $packet.Canonical '"schema": "grace.wdu.branch-lifecycle/v1"' `
            '"schema": "grace.wdu.branch-lifecycle/v1", "unexpectedRoot": true'
        Assert-ValidatorFails $packet 'malformed predicate properties|malformed JSON properties'
        $packet = New-Packet 'unknown-projection-field'
        Replace-Text (Join-Path $packet.Issues 'issue-843.md') '"schema": "grace.wdu.lifecycle-projection/v1"' `
            '"schema": "grace.wdu.lifecycle-projection/v1", "unexpectedNested": true'
        Assert-ValidatorFails $packet 'malformed predicate properties|malformed JSON properties'
    }

    Invoke-Case 'copied selected-Reference lifecycle sequence and active history prose fail' {
        $packet = New-Packet 'copied-selected-sequence'
        Add-OutsideProjection $packet.Adr 'For an already selected Reference, prove selected Branch, record terminal completion, then continue.'
        Assert-ValidatorFails $packet 'competing lifecycle source outside its projection'
        $packet = New-Packet 'active-history-prose'
        Add-OutsideProjection $packet.Adr @'
<!-- grace:wdu-lifecycle-historical-evidence:start -->
Historical supersession reference: publish before cleanup.
<!-- grace:wdu-lifecycle-historical-evidence:end -->
'@
        Assert-ValidatorFails $packet 'historical evidence must be the exact generated PR #873 reference'
    }

    Invoke-Case 'staging write failure preserves inputs and prior output packet' {
        $packet = New-Packet 'staging-failure'
        $inputs = @($packet.Canonical, $packet.Adr) + @(Get-ChildItem $packet.Issues -File | Select-Object -ExpandProperty FullName)
        $before = @{}; foreach ($path in $inputs) { $before[$path] = Get-FileDigest $path }
        $output = Join-Path $packet.Root 'failed output'
        $env:GRACE_WDU_RENDER_FAIL_AFTER_STAGING_WRITE = '1'
        try { & $validatorPath -CanonicalPath $packet.Canonical -ProjectionPath $packet.Adr -OfflineIssueBodyPath (Get-ChildItem $packet.Issues -File).FullName -RenderOutputPath $output; throw 'expected failure' } catch { }
        finally { Remove-Item Env:GRACE_WDU_RENDER_FAIL_AFTER_STAGING_WRITE -ErrorAction SilentlyContinue }
        Assert-True (-not (Test-Path -LiteralPath $output)) 'failed staging published a partial output packet'
        foreach ($path in $inputs) { Assert-True ((Get-FileDigest $path) -eq $before[$path]) "input changed $path" }
    }

    Invoke-Case 'Windows junction alias render root is rejected without changing inputs' {
        if (-not $IsWindows) { return }
        $packet = New-Packet 'junction-alias'
        $inputs = @(Get-ChildItem $packet.Issues -File | Select-Object -ExpandProperty FullName)
        $before = @{}; foreach ($path in $inputs) { $before[$path] = Get-FileDigest $path }
        $alias = Join-Path $packet.Root 'render-root-alias'
        New-Item -ItemType Junction -Path $alias -Target $packet.Issues | Out-Null
        try {
            & $validatorPath -CanonicalPath $packet.Canonical -ProjectionPath $packet.Adr -OfflineIssueBodyPath $inputs -RenderOutputPath $alias
            throw 'Expected junction render-root rejection.'
        } catch {
            Assert-True ($_.Exception.Message -match 'render output directory must not already exist|overwrite an input artifact') 'junction alias diagnostic'
        }
        foreach ($path in $inputs) { Assert-True ((Get-FileDigest $path) -eq $before[$path]) "junction alias changed $path" }
    }

    Invoke-Case 'WDU-LC-100 closed disallowed-marker effects and evidence retention fail independently' {
        foreach ($mutation in @(
                '["retainPending"]',
                '["retainEvidence","retainPending","attemptPublishSelectedBranch"]',
                '["retainEvidence","retainPending","attemptTerminalRecording"]')) {
            $packet = New-Packet "closed-row-$([Guid]::NewGuid().ToString('N'))"
            $text = [IO.File]::ReadAllText($packet.Canonical)
            $changed = [regex]::Replace($text, '("id":"WDU-LC-100".*?"requiredActions":)\["retainEvidence","retainPending"\]', {
                    param($match)
                    return "$($match.Groups[1].Value)$mutation"
                }, 1)
            if ($changed -ceq $text) { throw 'WDU-LC-100 action fixture anchor not found' }
            [IO.File]::WriteAllText($packet.Canonical, $changed, [Text.UTF8Encoding]::new($false))
            Assert-ValidatorFails $packet 'WDU-LC-100.*disallowed marker cell'
        }
    }

    Invoke-Case 'closed JSON schema rejects nested shape, order, scalar kinds, route placement, and duplicate candidate' {
        $packet = New-Packet 'wrong-encoding-shape'
        Replace-Text $packet.Canonical '"jsonShape":{"kind":"one","value":"<concrete-enum-member>"}' `
            '"jsonShape":{"kind":"set","value":"<concrete-enum-member>","extra":true}'
        Assert-ValidatorFails $packet 'encoding/one/jsonShape|malformed JSON properties'

        $packet = New-Packet 'wrong-order'
        Replace-Text $packet.Canonical '"conditionalExactCleanup",' `
            '"typedBranchPublicationOrProof",'
        Assert-ValidatorFails $packet 'lifecycle order is not the exact canonical member set'

        $packet = New-Packet 'wrong-nested-array-kind'
        Replace-Text $packet.Canonical '"values":["<concrete-enum-member>"]' '"values":"<concrete-enum-member>"'
        Assert-ValidatorFails $packet 'encoding/set/jsonShape/values must be a Array JSON value'

        $packet = New-Packet 'string-plan-count'
        Replace-Text $packet.Canonical '"canonicalRowCount": 67' '"canonicalRowCount": "67"'
        Assert-ValidatorFails $packet 'canonicalRowCount must be a Number JSON value'

        $packet = New-Packet 'decimal-plan-count'
        Replace-Text $packet.Canonical '"canonicalApplicabilityKeyCount": 254' '"canonicalApplicabilityKeyCount": 254.5'
        Assert-ValidatorFails $packet 'canonicalApplicabilityKeyCount must be an integer JSON number'

        $packet = New-Packet 'boolean-plan-count'
        Replace-Text $packet.Canonical '"canonicalRowCount": 67' '"canonicalRowCount": true'
        Assert-ValidatorFails $packet 'canonicalRowCount must be a Number JSON value'

        $packet = New-Packet 'missing-route-field'
        Replace-Text $packet.Canonical '"resultingMarker":"exact","nextRows":["WDU-LC-207"' '"resultingMarker":"exact","routes":["WDU-LC-207"'
        Assert-ValidatorFails $packet 'routing row requires nonempty nextRows|missing JSON property|unknown row property'

        $packet = New-Packet 'misplaced-terminal-result-marker'
        Replace-Text $packet.Canonical '"durableResult":"existingTerminal","outcome":"Unchanged"' `
            '"durableResult":"existingTerminal","outcome":"Unchanged","resultingMarker":"missing"'
        Assert-ValidatorFails $packet 'resultingMarker is misplaced'

        $packet = New-Packet 'duplicate-schema-candidate'
        Add-OutsideProjection $packet.Canonical '```json
{"schema":"grace.wdu.branch-lifecycle/v1","Schema":"grace.wdu.branch-lifecycle/v1"}
```'
        Assert-ValidatorFails $packet 'duplicate-equivalent JSON property|malformed JSON candidate'
    }

    Invoke-Case 'closed competing-source scanner rejects all branch proof families and history variants' {
        foreach ($sentence in @(
                'On retry, prove the current Branch is unchanged, then record terminal completion.',
                'For Reference previous Branch, publish the selected Branch, prove publication, then record terminal completion.',
                'For selected Reference, prove selected Branch, then record terminal completion.',
                'On retry, the first applicable write is terminal recording before completion.',
                'Clean the exact marker, publish the selected Branch, prove publication, and record terminal completion.')) {
            $packet = New-Packet "competing-$([Guid]::NewGuid().ToString('N'))"
            Add-OutsideProjection $packet.Adr $sentence
            Assert-ValidatorFails $packet 'competing lifecycle source outside its projection'
        }
        foreach ($history in @(
                'Historical supersession reference: [PR 873](https://github.com/ScottArbeit/Grace/pull/873).',
                'Historical supersession reference: [PR #873](https://example.test/873).',
                'Historical supersession reference: [PR #873](https://github.com/ScottArbeit/Grace/pull/873). Extra active sentence.')) {
            $packet = New-Packet "history-$([Guid]::NewGuid().ToString('N'))"
            Add-OutsideProjection $packet.Adr "<!-- grace:wdu-lifecycle-historical-evidence:start -->`n$history`n<!-- grace:wdu-lifecycle-historical-evidence:end -->"
            Assert-ValidatorFails $packet 'historical evidence must be the exact generated PR #873 reference'
        }
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "WDU lifecycle validator tests: passed=$script:Passed failed=$script:Failed"
if ($script:Failed -ne 0) { exit 1 }
