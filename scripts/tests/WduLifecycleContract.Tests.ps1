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
        Assert-True ($contract.RowIds.Count -eq 67) 'the canonical lifecycle has 67 rows'
        Assert-True ($contract.ApplicabilityKeys.Count -eq 254) 'the canonical lifecycle has 254 disjoint applicability keys'
        Assert-True ($contract.Digest -match '^[0-9a-f]{64}$') 'the normalized digest is lowercase SHA-256'
    }

    Invoke-Case 'normalizes LF and CRLF only for the digest' {
        $lf = New-CanonicalCopy 'lf'; $crlf = New-CanonicalCopy 'crlf'
        $text = (Get-TestText $lf).Replace("`r`n", "`n").Replace("`r", "`n")
        Set-TestText $lf $text
        Set-TestText $crlf $text.Replace("`n", "`r`n")
        Assert-True ((Read-WduLifecycleContract $lf).Digest -ceq (Read-WduLifecycleContract $crlf).Digest) 'line ending variants have one digest'
    }

    Invoke-Case 'a structurally valid semantic edit compiles and changes the digest' {
        $copy = New-CanonicalCopy 'semantic-edit'
        $original = Read-WduLifecycleContract $copy
        Set-TestText $copy (Replace-Once (Get-TestText $copy) 'retainMarkerEvidence' 'retainedMarkerEvidence')
        $changed = Read-WduLifecycleContract $copy
        Assert-True ($changed.Digest -cne $original.Digest) 'semantic data change changes the digest'
        Assert-True ($changed.RowIds.Count -eq 67) 'semantic data change remains structurally compilable'
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
        Assert-True ($contract.ApplicabilityKeys.Count -eq 254) 'unrelated JSON never participates in object parsing'
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

    foreach ($mutation in @(
        @{ Name = 'duplicate applicability'; Old = '"trigger":{"kind":"one","value":"exactSameOperationAdoption"},"marker":{"kind":"one","value":"exact"}'; New = '"trigger":{"kind":"one","value":"missingMarkerFreshAdmission"},"marker":{"kind":"one","value":"missing"}'; Reason = "rows 'WDU-LC-200' and 'WDU-LC-201'" },
        @{ Name = 'duplicate row ID'; Old = '"id":"WDU-LC-201"'; New = '"id":"WDU-LC-200"'; Reason = "duplicate row ID 'WDU-LC-200'" },
        @{ Name = 'dangling graph target'; Old = '"WDU-LC-207","WDU-LC-208"'; New = '"WDU-LC-missing","WDU-LC-208"'; Reason = 'dangling nextRows target' },
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
