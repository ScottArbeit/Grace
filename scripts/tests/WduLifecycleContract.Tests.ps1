Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$modulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleContract.psm1'
$canonicalPath = Join-Path $repositoryRoot 'docs/Working Directory Update.md'

Import-Module $modulePath -Force

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-ExactSequence {
    param([string[]] $Actual, [string[]] $Expected, [string] $Message)
    Assert-True ($Actual.Count -eq $Expected.Count) "$Message count"
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-True ($Actual[$index] -ceq $Expected[$index]) "$Message index $index"
    }
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

function Write-TestDocument {
    param([string] $Path, [string] $Content)
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-CanonicalCopy {
    param([string] $Name)
    $path = Join-Path $script:TestRoot "$Name.md"
    [IO.File]::Copy($canonicalPath, $path, $true)
    return $path
}

function Get-TestText {
    param([string] $Path)
    return [IO.File]::ReadAllText($Path)
}

function Set-TestText {
    param([string] $Path, [string] $Text)
    Write-TestDocument $Path $Text
}

function Replace-Once {
    param([string] $Text, [string] $Old, [string] $New)
    $index = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Test anchor not found: $Old" }
    return $Text.Remove($index, $Old.Length).Insert($index, $New)
}

function Replace-OnceAfter {
    param([string] $Text, [string] $Anchor, [string] $Old, [string] $New)
    $anchorIndex = $Text.IndexOf($Anchor, [StringComparison]::Ordinal)
    if ($anchorIndex -lt 0) { throw "Test anchor not found: $Anchor" }
    $index = $Text.IndexOf($Old, $anchorIndex, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Test anchor not found after '$Anchor': $Old" }
    return $Text.Remove($index, $Old.Length).Insert($index, $New)
}

function Assert-Fails {
    param([scriptblock] $Body, [string] $Contains)
    try { & $Body | Out-Null }
    catch {
        Assert-True $_.Exception.Message.Contains($Contains, [StringComparison]::Ordinal) "failure should contain '$Contains'"
        return
    }
    throw "Expected failure containing '$Contains'"
}

$script:Passed = 0
$script:Failed = 0
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("wdu-lifecycle-contract-" + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null

try {
    Invoke-Case 'exports only the contract reader' {
        $exports = @(Get-Command -Module WduLifecycleContract | Select-Object -ExpandProperty Name)
        Assert-True ($exports.Count -eq 1 -and $exports[0] -ceq 'Read-WduLifecycleContract') 'module exports only Read-WduLifecycleContract'
    }

    Invoke-Case 'compiles the canonical contract through a path with spaces and Unicode' {
        $directory = Join-Path $script:TestRoot 'path with spaces é'
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $copy = Join-Path $directory 'canonical lifecycle.md'
        [IO.File]::Copy($canonicalPath, $copy, $true)
        $contract = Read-WduLifecycleContract -Path $copy
        Assert-True ($contract.RowIds.Count -eq 70) 'the canonical lifecycle has 70 rows'
        Assert-True ($contract.RowsById.Count -eq 70) 'the row index has one exact entry for every parsed row'
        Assert-True ($contract.RowIds.Count -eq $contract.RowsById.Count) 'the returned row IDs and row index have equal cardinality'
        Assert-True ($contract.ApplicabilityKeys.Count -eq 260) 'the canonical lifecycle has 260 disjoint applicability keys'
        Assert-True ($contract.SchemaVersion -ceq 'grace.wdu.lifecycle-compiler-result/v1') 'the compiler result has its closed schema version'
        Assert-True ($contract.Digest -ceq 'ae3a77e28886485b49361d8836f040691e9f99228919cef87fac19b42e989d73') 'the complete machine plan has its pinned replacement digest'
        Assert-True ($contract.AssignmentDigest -ceq '20e329bd3aa4459a01f4ed3c6ec12cf365c86df3538b0323400639b90eeee877') 'the artifact assignment vector has its deterministic digest'
        Assert-True ($contract.Requirements.Count -eq 19 -and $contract.RequirementOwners.Count -eq 19) 'the result exposes all ordered requirement owners'
        Assert-True ($contract.Artifacts.Count -eq 15 -and $contract.ArtifactAssignments.Count -eq 15) 'the result exposes all ordered artifact assignments'
        Assert-True ($contract.Counts.decisionCount -eq 9 -and $contract.Counts.requirementCount -eq 19 -and $contract.Counts.artifactCount -eq 15 -and $contract.Counts.rowCount -eq 70 -and $contract.Counts.applicabilityKeyCount -eq 260) 'the result exposes exact machine counts'
        Assert-ExactSequence @($contract.DecisionIds) @('DEC-001','DEC-002','DEC-003','DEC-004','DEC-005','DEC-006','DEC-007','DEC-008','DEC-009') 'decision order'
        Assert-ExactSequence @($contract.Requirements | ForEach-Object Id) @('REQ-001','REQ-002','REQ-003','REQ-004','REQ-005','REQ-006','REQ-007','REQ-008','REQ-009','REQ-010','REQ-011','REQ-012','REQ-013','REQ-014','REQ-015','REQ-016','REQ-017','REQ-018','REQ-019') 'requirement order'
        Assert-ExactSequence @($contract.Artifacts | ForEach-Object Id) @('adr-0011','epic-835','issue-842','issue-843','issue-846','issue-869','issue-898','issue-928','issue-921','issue-922','issue-923','issue-900','issue-901','issue-871','issue-872') 'artifact order'
    }

    Invoke-Case 'normalizes LF and CRLF only for the digest' {
        $lf = New-CanonicalCopy 'lf'; $crlf = New-CanonicalCopy 'crlf'
        $text = (Get-TestText $lf).Replace("`r`n", "`n").Replace("`r", "`n")
        Set-TestText $lf $text
        Set-TestText $crlf $text.Replace("`n", "`r`n")
        Assert-True ((Read-WduLifecycleContract $lf).Digest -ceq (Read-WduLifecycleContract $crlf).Digest) 'line ending variants have one digest'
    }

    Invoke-Case 'rejects a structurally valid semantic edit without its matching complete-plan digest' {
        $copy = New-CanonicalCopy 'semantic-edit'
        Set-TestText $copy (Replace-Once (Get-TestText $copy) 'retainMarkerEvidence' 'retainedMarkerEvidence')
        Assert-Fails { Read-WduLifecycleContract $copy } 'canonicalContentDigest does not match the complete machine-owned plan'
    }

    Invoke-Case 'ignores unrelated fenced JSON values and malformed samples' {
        $copy = New-CanonicalCopy 'unrelated-json'
        $samples = @(
            ('```json' + "`n" + '[]' + "`n" + '```'), ('```json' + "`n" + '42' + "`n" + '```'),
            ('```json' + "`n" + 'null' + "`n" + '```'), ('```json' + "`n" + '{' + "`n" + '```'),
            ('```json' + "`n" + '{"schema":"grace.wdu.branch-lifecycle/v1"}' + "`n" + '```'), ('```json' + "`n`n" + '```')
        )
        Set-TestText $copy ((Get-TestText $copy) + "`n# Unicode outside block: é`n" + ($samples -join "`n"))
        $contract = Read-WduLifecycleContract $copy
        Assert-True ($contract.ApplicabilityKeys.Count -eq 260) 'unrelated JSON never participates in object parsing'
    }

    Invoke-Case 'makes fresh and adopted reconciliation plus zero-action routes structurally reachable' {
        $contract = Read-WduLifecycleContract $canonicalPath
        Assert-True ($contract.RowsById['WDU-LC-200'].requiredActions -contains 'reconcileFreshAdmissionAsNeedsApplyOnly') 'fresh admission is NeedsApply only'
        Assert-True ($contract.RowsById['WDU-LC-201'].requiredActions -contains 'reconcileExactAdoptionAsNeedsApplyOrAlreadySatisfied') 'exact adoption can converge mixed partial progress'
        Assert-True ($contract.RowsById['WDU-LC-209'].nextRows -contains 'WDU-LC-006') 'zero-action success reaches pending completion'
        Assert-True ($contract.RowsById['WDU-LC-209'].nextRows -contains 'WDU-LC-007') 'zero-action completion failure is reachable'
        Assert-True ($contract.RowsById['WDU-LC-209'].nextRows -contains 'WDU-LC-207') 'zero-action cancellation cannot bypass owned-marker rejection'
    }

    Invoke-Case 'rejects a cancellation bypass from the zero-action verified-root boundary' {
        $copy = New-CanonicalCopy 'zero-action-cancellation-bypass'
        $mutated = Replace-Once -Text (Get-TestText $copy) -Old '"WDU-LC-207","WDU-LC-208","WDU-LC-210","WDU-LC-006","WDU-LC-007"' -New '"WDU-LC-208","WDU-LC-210","WDU-LC-006","WDU-LC-007"'
        Set-TestText $copy $mutated
        Assert-Fails { Read-WduLifecycleContract $copy } "row 'WDU-LC-209' must route to 'WDU-LC-207'"
    }

    Invoke-Case 'rejects a post-verified-root completion failure that downgrades to Rejected' {
        $copy = New-CanonicalCopy 'verified-root-rejected'
        $mutated = Replace-Once -Text (Get-TestText $copy) -Old '"workingFiles":"verifiedRelevantTarget","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"UpdateIncomplete","exitClass":"nonzero","doctorGuidance":false},' -New '"workingFiles":"verifiedRelevantTarget","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},'
        Set-TestText $copy $mutated
        Assert-Fails { Read-WduLifecycleContract $copy } "row 'WDU-LC-007' must classify post-VerifiedLocalRoot completion failure"
    }

    Invoke-Case 'maps ephemeral bytesChanged to distinct DirectoryVersion terminals while Reference remains unambiguous' {
        $contract = Read-WduLifecycleContract $canonicalPath
        Assert-True ($contract.RowsById['WDU-LC-026'].outcome -ceq 'Updated') 'missing-marker bytesChanged terminal is Updated'
        Assert-True ($contract.RowsById['WDU-LC-028'].outcome -ceq 'Unchanged') 'missing-marker bytesUnchanged terminal is Unchanged'
        Assert-True ($contract.RowsById['WDU-LC-036'].outcome -ceq 'Updated') 'exact-marker bytesChanged terminal is Updated'
        Assert-True ($contract.RowsById['WDU-LC-038'].outcome -ceq 'Unchanged') 'exact-marker bytesUnchanged terminal is Unchanged'
        Assert-True ($contract.RowsById['WDU-LC-020'].match.trigger.value -ceq 'afterSqliteLocalCompletion') 'Reference previous retains its ordinary completion trigger'
        Assert-True ($contract.RowsById['WDU-LC-033'].match.trigger.value -ceq 'afterSqliteLocalCompletion') 'Reference selected retains its ordinary completion trigger'
    }

    Invoke-Case 'rejects collapsing the ephemeral bytesChanged discriminator' {
        $copy = New-CanonicalCopy 'collapsed-bytes-changed-discriminator'
        $old = '"id":"WDU-LC-028","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesUnchanged"}'
        $new = '"id":"WDU-LC-028","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesChanged"}'
        Set-TestText $copy (Replace-Once (Get-TestText $copy) $old $new)
        Assert-Fails { Read-WduLifecycleContract $copy } 'duplicate applicability key'
    }

    Invoke-Case 'rejects deleting the ephemeral bytesChanged false terminal' {
        $copy = New-CanonicalCopy 'missing-bytes-unchanged-terminal'
        $mutated = [regex]::Replace((Get-TestText $copy), '(?m)^    \{"id":"WDU-LC-028".*\r?\n', '', 1)
        Set-TestText $copy $mutated
        Assert-Fails { Read-WduLifecycleContract $copy } 'rows[].id must contain exactly 70 values'
    }

    Invoke-Case 'leaves input bytes unchanged on success and failure' {
        $success = New-CanonicalCopy 'read-only-success'; $failure = New-CanonicalCopy 'read-only-failure'
        $beforeSuccess = (Get-FileHash -LiteralPath $success -Algorithm SHA256).Hash
        Read-WduLifecycleContract $success | Out-Null
        Assert-True ($beforeSuccess -ceq (Get-FileHash -LiteralPath $success -Algorithm SHA256).Hash) 'success is read-only'
        Set-TestText $failure ((Get-TestText $failure).Replace('<!-- grace:wdu-lifecycle-contract:end -->', ''))
        $beforeFailure = (Get-FileHash -LiteralPath $failure -Algorithm SHA256).Hash
        Assert-Fails { Read-WduLifecycleContract $failure } 'marker pair'
        Assert-True ($beforeFailure -ceq (Get-FileHash -LiteralPath $failure -Algorithm SHA256).Hash) 'failure is read-only'
    }

    foreach ($markerCase in @(
        @{ Name = 'missing markers'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-contract:start -->', '') }; Reason = 'marker pair' },
        @{ Name = 'duplicate markers'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-contract:start -->', "<!-- grace:wdu-lifecycle-contract:start -->`n<!-- grace:wdu-lifecycle-contract:start -->") }; Reason = 'marker pair' },
        @{ Name = 'reversed markers'; Mutate = { param($text) $text.Replace('<!-- grace:wdu-lifecycle-contract:start -->', '<!-- temporary marker -->').Replace('<!-- grace:wdu-lifecycle-contract:end -->', '<!-- grace:wdu-lifecycle-contract:start -->').Replace('<!-- temporary marker -->', '<!-- grace:wdu-lifecycle-contract:end -->') }; Reason = 'markers are reversed' }
    )) {
        Invoke-Case $markerCase.Name {
            $copy = New-CanonicalCopy $markerCase.Name.Replace(' ', '-')
            Set-TestText $copy (& $markerCase.Mutate (Get-TestText $copy))
            Assert-Fails { Read-WduLifecycleContract $copy } $markerCase.Reason
        }
    }

    foreach ($payload in @('[]', '42', 'null', '{')) {
        Invoke-Case "rejects marked non-object or malformed JSON $payload" {
            $copy = New-CanonicalCopy ('payload-' + [guid]::NewGuid().ToString('N'))
            $text = [regex]::Replace((Get-TestText $copy), '(?s)(?<=```json\n)\{.*?(?=\n```)', $payload, 1)
            Set-TestText $copy $text
            Assert-Fails { Read-WduLifecycleContract $copy } 'marked'
        }
    }

    Invoke-Case 'rejects a rows object that PowerShell could enumerate' {
        $copy = New-CanonicalCopy 'rows-object'
        $text = [regex]::Replace((Get-TestText $copy), '(?s)"rows":\s*\[.*?\]\s*\n\}', '"rows": {"id":"not-an-array"}' + "`n}", 1)
        Set-TestText $copy $text
        try { Read-WduLifecycleContract $copy | Out-Null }
        catch {
            Assert-True $_.Exception.Message.Contains($copy, [StringComparison]::Ordinal) 'rows diagnostic includes the canonical path'
            Assert-True $_.Exception.Message.Contains('rows must be a JSON array', [StringComparison]::Ordinal) 'rows diagnostic names the raw array requirement'
            return
        }
        throw 'Expected a rows-object failure'
    }

    foreach ($mutation in @(
        @{ Name = 'unknown root property'; Old = '"schema": "grace.wdu.branch-lifecycle/v1",'; New = '"schema": "grace.wdu.branch-lifecycle/v1", "unknown": true,'; Reason = 'unknown property' },
        @{ Name = 'wrong token kind'; Old = '"doctorCommand": "grace doctor --repair-local-state"'; New = '"doctorCommand": true'; Reason = 'doctorCommand must be a JSON string' },
        @{ Name = 'duplicate case property'; Old = '"schema": "grace.wdu.branch-lifecycle/v1",'; New = '"schema": "grace.wdu.branch-lifecycle/v1", "Schema": "x",'; Reason = 'duplicate case-equivalent' },
        @{ Name = 'nested duplicate case property'; Old = '"invocation":{"kind":"one","value":"initial"}'; New = '"invocation":{"kind":"one","Kind":"one","value":"initial"}'; Reason = 'duplicate case-equivalent' },
        @{ Name = 'grammar shape wrong token kind'; Old = '"kind":"one","value":"<concrete-enum-member>"'; New = '"kind":"one","value":true'; Reason = 'jsonShape.value must be a JSON string' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    foreach ($mutation in @(
        @{ Name = 'wrong-case root property'; Old = '"schema": "grace.wdu.branch-lifecycle/v1"'; New = '"Schema": "grace.wdu.branch-lifecycle/v1"'; Reason = "unknown property 'Schema'" },
        @{ Name = 'wrong-case row property'; Old = '"id":"WDU-LC-200"'; New = '"Id":"WDU-LC-200"'; Reason = "unknown property 'Id'" },
        @{ Name = 'wrong-case predicate property'; Old = '"invocation":{"kind":"one","value":"initial"}'; New = '"invocation":{"Kind":"one","value":"initial"}'; Reason = "unknown property 'Kind'" },
        @{ Name = 'wrong-case optional resulting marker'; Old = '"resultingMarker":"exact"'; New = '"ResultingMarker":"exact"'; Reason = "unknown property 'ResultingMarker'" },
        @{ Name = 'wrong-case optional next rows'; Old = '"nextRows":["WDU-LC-207","WDU-LC-208"'; New = '"NextRows":["WDU-LC-207","WDU-LC-208"'; Reason = "unknown property 'NextRows'" }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    foreach ($mutation in @(
        @{ Name = 'unknown predicate value'; Old = '"value":"initial"'; New = '"value":"unknown"'; Reason = 'is not declared' },
        @{ Name = 'unknown predicate kind'; Old = '"invocation":{"kind":"one","value":"initial"}'; New = '"invocation":{"kind":"wildcard","value":"initial"}'; Reason = 'unknown predicate kind' },
        @{ Name = 'empty predicate set'; Old = '"values":["differentOperation","malformed","unsupported","unreadable","exactCleanupFailed"]'; New = '"values":[]'; Reason = 'must not be empty' },
        @{ Name = 'duplicate predicate set'; Old = '"values":["differentOperation","malformed","unsupported","unreadable","exactCleanupFailed"]'; New = '"values":["differentOperation","differentOperation"]'; Reason = 'duplicate value' },
        @{ Name = 'cross axis aggregate'; Old = '"marker":{"kind":"aggregate","name":"none"}'; New = '"marker":{"kind":"aggregate","name":"persisted"}'; Reason = 'is not declared' },
        @{ Name = 'missing aggregate'; Old = '"marker":{"kind":"aggregate","name":"none"}'; New = '"marker":{"kind":"aggregate","name":"missing"}'; Reason = 'is not declared' },
        @{ Name = 'malformed predicate shape'; Old = '"invocation":{"kind":"one","value":"initial"}'; New = '"invocation":{"kind":"one","values":["initial"]}'; Reason = 'unknown property' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    Invoke-Case 'rejects a wrong-case predicate kind with the canonical predicate location' {
        $copy = New-CanonicalCopy 'wrong-case-predicate-kind'
        Set-TestText $copy (Replace-Once (Get-TestText $copy) '"invocation":{"kind":"one","value":"initial"}' '"invocation":{"kind":"One","value":"initial"}')
        try { Read-WduLifecycleContract $copy | Out-Null }
        catch {
            Assert-True $_.Exception.Message.Contains($copy, [StringComparison]::Ordinal) 'predicate-kind diagnostic includes the canonical path'
            Assert-True $_.Exception.Message.Contains('row WDU-LC-200.invocation', [StringComparison]::Ordinal) 'predicate-kind diagnostic includes the row and axis'
            Assert-True $_.Exception.Message.Contains("unknown predicate kind 'One'", [StringComparison]::Ordinal) 'predicate-kind diagnostic names the exact discriminator'
            return
        }
        throw 'Expected a wrong-case predicate-kind failure'
    }

    Invoke-Case 'rejects a wrong-case ordered applicability axis with the canonical overlap location' {
        $copy = New-CanonicalCopy 'wrong-case-applicability-axis'
        Set-TestText $copy (Replace-Once (Get-TestText $copy) '"applicabilityKey": ["invocation"' '"applicabilityKey": ["Invocation"')
        try { Read-WduLifecycleContract $copy | Out-Null }
        catch {
            Assert-True $_.Exception.Message.Contains($copy, [StringComparison]::Ordinal) 'applicability-axis diagnostic includes the canonical path'
            Assert-True $_.Exception.Message.Contains('machineGrammar.overlap.applicabilityKey', [StringComparison]::Ordinal) 'applicability-axis diagnostic includes the overlap declaration'
            Assert-True $_.Exception.Message.Contains('predicate axes', [StringComparison]::Ordinal) 'applicability-axis diagnostic names the ordered-axis requirement'
            return
        }
        throw 'Expected a wrong-case applicability-axis failure'
    }

    foreach ($mutation in @(
        @{ Name = 'duplicate applicability'; Old = '"trigger":{"kind":"one","value":"exactSameOperationAdoption"},"marker":{"kind":"one","value":"exact"}'; New = '"trigger":{"kind":"one","value":"missingMarkerFreshAdmission"},"marker":{"kind":"one","value":"missing"}'; Reason = "rows 'WDU-LC-200' and 'WDU-LC-201'" },
        @{ Name = 'duplicate row ID'; Old = '"id":"WDU-LC-201"'; New = '"id":"WDU-LC-200"'; Reason = "duplicate row ID 'WDU-LC-200'" },
        @{ Name = 'case-equivalent row ID'; Old = '"id":"WDU-LC-201"'; New = '"id":"wdu-lc-200"'; Reason = "duplicate row ID 'wdu-lc-200'" },
        @{ Name = 'routing terminal result'; Old = '"outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows"'; New = '"outcome":"Updated","exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows"'; Reason = 'routing row must not have an outcome' },
        @{ Name = 'routing without successor'; Old = '"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]'; New = '"doctorGuidance":null,"resultingMarker":"exact"'; Reason = 'terminal row must have an outcome' },
        @{ Name = 'terminal successor'; Old = '"doctorGuidance":true},'; New = '"doctorGuidance":true,"nextRows":["WDU-LC-203"]},'; Reason = 'routing row must not have an outcome' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    foreach ($mutation in @(
        @{ Name = 'dangling graph target'; Old = '"WDU-LC-207","WDU-LC-208"'; New = '"WDU-LC-missing","WDU-LC-208"'; Reason = 'dangling nextRows target' },
        @{ Name = 'wrong-case graph target'; Old = '"WDU-LC-207","WDU-LC-208"'; New = '"wdu-lc-207","WDU-LC-208"'; Reason = 'dangling nextRows target' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            $text = Replace-OnceAfter (Get-TestText $copy) '"id":"WDU-LC-200"' $mutation.Old $mutation.New
            Set-TestText $copy $text
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    Invoke-Case 'pins the exact ordered lifecycle vector and graph closure' {
        $vector = @('WDU-LC-200','WDU-LC-201','WDU-LC-202','WDU-LC-203','WDU-LC-204','WDU-LC-205','WDU-LC-206','WDU-LC-207','WDU-LC-208','WDU-LC-209','WDU-LC-210','WDU-LC-211','WDU-LC-212','WDU-LC-001','WDU-LC-005','WDU-LC-002','WDU-LC-004','WDU-LC-006','WDU-LC-007','WDU-LC-003','WDU-LC-010','WDU-LC-011','WDU-LC-012','WDU-LC-013','WDU-LC-014','WDU-LC-015','WDU-LC-020','WDU-LC-021','WDU-LC-022','WDU-LC-023','WDU-LC-024','WDU-LC-025','WDU-LC-026','WDU-LC-027','WDU-LC-028','WDU-LC-030','WDU-LC-031','WDU-LC-032','WDU-LC-033','WDU-LC-034','WDU-LC-035','WDU-LC-036','WDU-LC-037','WDU-LC-038','WDU-LC-100','WDU-LC-101','WDU-LC-102','WDU-LC-103','WDU-LC-104','WDU-LC-105','WDU-LC-106','WDU-LC-107','WDU-LC-108','WDU-LC-109','WDU-LC-115','WDU-LC-116','WDU-LC-110','WDU-LC-111','WDU-LC-112','WDU-LC-113','WDU-LC-114','WDU-LC-120','WDU-LC-121','WDU-LC-122','WDU-LC-123','WDU-LC-130','WDU-LC-140','WDU-LC-141','WDU-LC-142','WDU-LC-143')
        $contract = Read-WduLifecycleContract $canonicalPath
        Assert-ExactSequence @($contract.RowIds) $vector 'lifecycle row vector'
        Assert-True ($contract.ApplicabilityKeys.Count -eq 260) 'lifecycle vector still expands to 260 keys'
    }

    foreach ($mutation in @(
        @{ Name = 'swapped WDU-LC-200 and WDU-LC-201 vector entries'; Old = '"WDU-LC-200","WDU-LC-201","WDU-LC-202"'; New = '"WDU-LC-201","WDU-LC-200","WDU-LC-202"'; Reason = 'rows[].id must match the declared canonical order' },
        @{ Name = 'renamed WDU-LC-003 row'; Old = '"id":"WDU-LC-003"'; New = '"id":"WDU-LC-003-renamed"'; Reason = 'rows[].id must match the declared canonical order' },
        @{ Name = 'removed WDU-LC-028 row'; Old = '"id":"WDU-LC-028"'; New = '"id":"WDU-LC-028-removed"'; Reason = 'rows[].id must match the declared canonical order' },
        @{ Name = 'wrong-case lifecycle row ID'; Old = '"id":"WDU-LC-201"'; New = '"id":"wdu-lc-201"'; Reason = 'rows[].id must match the declared canonical order' },
        @{ Name = 'terminal replay row mutation'; Old = '"row": "WDU-LC-003"'; New = '"row": "WDU-LC-004"'; Reason = 'terminalReplay.row must resolve exactly WDU-LC-003' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    Invoke-Case 'duplicate WDU-LC-207 successor' {
        $copy = New-CanonicalCopy 'duplicate-wdu-lc-207-successor'
        $text = Replace-OnceAfter (Get-TestText $copy) '"id":"WDU-LC-200"' '"WDU-LC-207","WDU-LC-208","WDU-LC-209"' '"WDU-LC-207","WDU-LC-207","WDU-LC-209"'
        Set-TestText $copy $text
        Assert-Fails { Read-WduLifecycleContract $copy } 'nextRows contains duplicate value'
    }

    Invoke-Case 'rejects an added declared trigger and lifecycle row' {
        $copy = New-CanonicalCopy 'added-declared-trigger-and-row'
        $text = Replace-Once (Get-TestText $copy) '"verifiedRootReadyForSqliteLocalCompletion"]' '"verifiedRootReadyForSqliteLocalCompletion","declaredExtraTrigger"]'
        $row = '{"id":"WDU-LC-999","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"declaredExtraTrigger"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},'
        $text = Replace-Once $text '    {"id":"WDU-LC-200"' ("    $row`n    {`"id`":`"WDU-LC-200`"")
        Set-TestText $copy $text
        Assert-Fails { Read-WduLifecycleContract $copy } 'rows[].id must contain exactly 70 values'
    }

    Invoke-Case 'returns exact requirement and artifact metadata without a prose registry' {
        $contract = Read-WduLifecycleContract $canonicalPath
        Assert-True ($contract.RequirementOwners['REQ-018'] -ceq '#928') 'replacement requirement ownership is machine owned'
        Assert-True ($contract.ArtifactAssignments.ContainsKey('issue-928')) 'replacement artifact identity is machine owned'
        Assert-True (-not $contract.ArtifactAssignments.ContainsKey('issue-920') -and -not $contract.ArtifactAssignments.ContainsKey('issue-929')) 'superseded and projection identities are excluded'
        Assert-ExactSequence @($contract.ArtifactAssignments['issue-921']) @('WDU-LC-200','WDU-LC-201','WDU-LC-209','WDU-LC-210') 'issue-921 assignment'
        Assert-True ($contract.AssignedRowIds.Count -eq 70) 'artifact assignments cover every lifecycle row'
    }

    foreach ($requirement in (Read-WduLifecycleContract $canonicalPath).Requirements) {
        Invoke-Case "rejects changed owner for $($requirement.Id)" {
            $copy = New-CanonicalCopy ("owner-" + $requirement.Id)
            $old = '"id":"' + $requirement.Id + '","owner":"' + $requirement.Owner + '"'
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $old ('"id":"' + $requirement.Id + '","owner":"#999"'))
            Assert-Fails { Read-WduLifecycleContract $copy } 'canonicalContentDigest does not match the complete machine-owned plan'
        }
    }

    foreach ($mutation in @(
        @{ Name = 'missing requirement'; Old = '{"id":"REQ-001","owner":"#923"},'; New = ''; Reason = 'machineMetadata must contain exactly 19 requirements' },
        @{ Name = 'duplicate requirement'; Old = '{"id":"REQ-001","owner":"#923"},'; New = '{"id":"REQ-001","owner":"#923"},{"id":"REQ-001","owner":"#923"},'; Reason = "duplicate ID 'REQ-001'" },
        @{ Name = 'reordered requirements'; Old = '{"id":"REQ-001","owner":"#923"},{"id":"REQ-002","owner":"#869"}'; New = '{"id":"REQ-002","owner":"#869"},{"id":"REQ-001","owner":"#923"}'; Reason = 'canonicalContentDigest does not match the complete machine-owned plan' },
        @{ Name = 'case changed requirement'; Old = '"id":"REQ-001"'; New = '"id":"req-001"'; Reason = 'canonicalContentDigest does not match the complete machine-owned plan' },
        @{ Name = 'extra requirement'; Old = '{"id":"REQ-001","owner":"#923"},'; New = '{"id":"REQ-000","owner":"#923"},{"id":"REQ-001","owner":"#923"},'; Reason = 'machineMetadata must contain exactly 19 requirements' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    foreach ($artifact in (Read-WduLifecycleContract $canonicalPath).Artifacts) {
        Invoke-Case "rejects changed artifact identity for $($artifact.Id)" {
            $copy = New-CanonicalCopy ("artifact-" + $artifact.Id)
            Set-TestText $copy (Replace-Once (Get-TestText $copy) ('"id":"' + $artifact.Id + '","rowIds"') ('"id":"unknown-' + $artifact.Id + '","rowIds"'))
            $reason = if ($artifact.Id -ceq 'issue-928') { 'artifact identities must include issue-928' } else { 'canonicalContentDigest does not match the complete machine-owned plan' }
            Assert-Fails { Read-WduLifecycleContract $copy } $reason
        }
    }

    foreach ($mutation in @(
        @{ Name = 'missing artifact'; Old = '{"id":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]},'; New = ''; Reason = 'machineMetadata must contain exactly 19 requirements and 15 artifacts' },
        @{ Name = 'duplicate artifact'; Old = '{"id":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]},'; New = '{"id":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]},{"id":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]},'; Reason = "duplicate ID 'issue-843'" },
        @{ Name = 'unknown assigned row'; Old = '"id":"issue-921","rowIds":["WDU-LC-200"'; New = '"id":"issue-921","rowIds":["WDU-LC-unknown"'; Reason = "has unknown row 'WDU-LC-unknown'" },
        @{ Name = 'duplicate assigned row'; Old = '"id":"issue-921","rowIds":["WDU-LC-200","WDU-LC-201"'; New = '"id":"issue-921","rowIds":["WDU-LC-200","WDU-LC-200"'; Reason = 'rowIds contains duplicate value' },
        @{ Name = 'reordered assigned rows'; Old = '"id":"issue-921","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-209","WDU-LC-210"]'; New = '"id":"issue-921","rowIds":["WDU-LC-201","WDU-LC-200","WDU-LC-209","WDU-LC-210"]'; Reason = 'canonicalContentDigest does not match the complete machine-owned plan' }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    Invoke-Case 'rejects reordered artifacts' {
        $copy = New-CanonicalCopy 'reordered-artifacts'
        $text = Get-TestText $copy
        $text = Replace-OnceAfter $text '"machineMetadata"' '"id":"adr-0011"' '"id":"temporary-artifact"'
        $text = Replace-OnceAfter $text '"machineMetadata"' '"id":"epic-835"' '"id":"adr-0011"'
        $text = Replace-OnceAfter $text '"machineMetadata"' '"id":"temporary-artifact"' '"id":"epic-835"'
        Set-TestText $copy $text
        Assert-Fails { Read-WduLifecycleContract $copy } 'canonicalContentDigest does not match the complete machine-owned plan'
    }

    Invoke-Case 'rejects missing assigned row coverage' {
        $copy = New-CanonicalCopy 'missing-assigned-row-coverage'
        $text = Replace-OnceAfter (Get-TestText $copy) '"id":"issue-900"' '"WDU-LC-026","WDU-LC-027","WDU-LC-028"' '"WDU-LC-026","WDU-LC-028"'
        Set-TestText $copy $text
        Assert-Fails { Read-WduLifecycleContract $copy } 'artifact assignments must cover every lifecycle row'
    }

    foreach ($mutation in @(
        @{ Name = 'wrong machine count'; Old = '"rowCount":70'; New = '"rowCount":71'; Reason = 'expectedCounts.rowCount must equal 70' },
        @{ Name = 'unknown metadata field'; Old = '"requirements": ['; New = '"unknown":true,"requirements": ['; Reason = "machineMetadata has unknown property 'unknown'" }
    )) {
        Invoke-Case $mutation.Name {
            $copy = New-CanonicalCopy $mutation.Name.Replace(' ', '-')
            Set-TestText $copy (Replace-Once (Get-TestText $copy) $mutation.Old $mutation.New)
            Assert-Fails { Read-WduLifecycleContract $copy } $mutation.Reason
        }
    }

    Invoke-Case 'rejects an object-valued requirements token in valid machine metadata' {
        $copy = New-CanonicalCopy 'wrong-metadata-raw-token'
        $text = Get-TestText $copy
        $metadataIndex = $text.IndexOf('"machineMetadata"', [StringComparison]::Ordinal)
        $requirementsIndex = $text.IndexOf('"requirements": [', $metadataIndex, [StringComparison]::Ordinal)
        $artifactsIndex = $text.IndexOf('"artifacts": [', $requirementsIndex, [StringComparison]::Ordinal)
        $separatorIndex = $text.LastIndexOf(',', $artifactsIndex)
        Assert-True ($metadataIndex -ge 0 -and $requirementsIndex -ge 0 -and $artifactsIndex -ge 0 -and $separatorIndex -gt $requirementsIndex) 'machine metadata anchors are present'
        $requirementsValueIndex = $requirementsIndex + '"requirements": '.Length
        $objectValuedRequirements = $text.Substring(0, $requirementsValueIndex) + '{}' + $text.Substring($separatorIndex)
        Assert-True ($objectValuedRequirements.Contains('"artifacts": [', [StringComparison]::Ordinal)) 'the valid raw-token fixture retains machine metadata artifacts'
        Set-TestText $copy $objectValuedRequirements
        Assert-Fails { Read-WduLifecycleContract $copy } 'machineMetadata.requirements must be a JSON array'
    }

    foreach ($predicateToken in @('true', '[]', '"initial"', 'null')) {
        Invoke-Case "wrapper reports a scoped non-object predicate $predicateToken" {
            $copy = New-CanonicalCopy ('predicate-token-' + [guid]::NewGuid().ToString('N'))
            Set-TestText $copy (Replace-Once (Get-TestText $copy) '"invocation":{"kind":"one","value":"initial"}' ('"invocation":' + $predicateToken))
            $wrapper = Join-Path $repositoryRoot 'scripts/validate-wdu-lifecycle-contract.ps1'
            $diagnostic = & pwsh -NoProfile -File $wrapper -Path $copy 2>&1
            Assert-True ($LASTEXITCODE -ne 0) 'wrapper exits nonzero for a non-object predicate'
            $message = $diagnostic -join "`n"
            Assert-True ($message.Contains($copy, [StringComparison]::Ordinal)) 'wrapper diagnostic includes the file path'
            Assert-True ($message.Contains('row WDU-LC-200.invocation', [StringComparison]::Ordinal)) 'wrapper diagnostic includes the row and axis'
            Assert-True ($message.Contains('must be a predicate object with kind', [StringComparison]::Ordinal)) 'wrapper diagnostic includes the reason'
        }
    }

    Invoke-Case 'thin wrapper returns a success model and a nonzero diagnostic' {
        $wrapper = Join-Path $repositoryRoot 'scripts/validate-wdu-lifecycle-contract.ps1'
        $success = & pwsh -NoProfile -File $wrapper -Path $canonicalPath 2>&1
        Assert-True ($LASTEXITCODE -eq 0) 'wrapper succeeds for canonical contract'
        Assert-True (($success -join "`n").Contains('WDU-LC-200', [StringComparison]::Ordinal)) 'wrapper projects the model'
        $failure = New-CanonicalCopy 'wrapper-failure'; Set-TestText $failure ((Get-TestText $failure).Replace('<!-- grace:wdu-lifecycle-contract:end -->', ''))
        $diagnostic = & pwsh -NoProfile -File $wrapper -Path $failure 2>&1
        Assert-True ($LASTEXITCODE -ne 0) 'wrapper exits nonzero for invalid contract'
        Assert-True (($diagnostic -join "`n").Contains('marker pair', [StringComparison]::Ordinal)) 'wrapper projects concise diagnostic'
    }
}
finally {
    if (Test-Path -LiteralPath $script:TestRoot) { Remove-Item -LiteralPath $script:TestRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
