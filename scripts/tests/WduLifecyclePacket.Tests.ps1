Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packetTest = Join-Path $repositoryRoot 'scripts/test-wdu-lifecycle-packet.ps1'
$artifactIds = @('adr-0011', 'issue-835', 'issue-842', 'issue-843', 'issue-846', 'issue-869', 'issue-898', 'issue-920', 'issue-921', 'issue-922', 'issue-923', 'issue-900', 'issue-901', 'issue-871', 'issue-872')
$expectedOwners = @('#923', '#869', '#837', '#839', '#869', '#898', '#922', '#922', '#838', '#838', '#871', '#871', '#900', '#921', '#842', '#871', '#846', '#920', '#923')
$primaryOwnerTableHeader = '| Requirement | Primary delivery issue | Companion proof, extension, or deferred disposition | #846 audit disposition |'
$primaryOwnerTableDivider = '| --- | --- | --- | --- |'
$auditTableHeader = '| Requirement | Primary delivery issue | Audit disposition |'
$auditTableDivider = '| --- | --- | --- |'

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

function Get-Ledger {
    param([bool] $PrimaryOwnerTable)
    $rows = for ($index = 0; $index -lt $expectedOwners.Count; $index++) {
        $requirement = 'REQ-{0:d3}' -f ($index + 1)
        if ($PrimaryOwnerTable) {
            "| $requirement requirement $($index + 1) | $($expectedOwners[$index]) | companion $($index + 1) | audit $($index + 1) |"
        }
        else {
            "| $requirement | $($expectedOwners[$index]) | audit $($index + 1) |"
        }
    }
    $header = if ($PrimaryOwnerTable) { $primaryOwnerTableHeader } else { $auditTableHeader }
    $divider = if ($PrimaryOwnerTable) { $primaryOwnerTableDivider } else { $auditTableDivider }
    return (@(
        '<!-- grace:wdu-requirements:start -->',
        '',
        $header,
        $divider
    ) + $rows + @(
        '',
        '<!-- grace:wdu-requirements:end -->'
    )) -join "`n"
}

function Write-TestText {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function New-Packet {
    param([string] $Name)
    $root = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    foreach ($artifact in $artifactIds) {
        $body = if ($artifact -eq 'issue-835') { "# $artifact`n`nCompletion claim: 19/19 requirements.`n`n$(Get-Ledger $true)" } elseif ($artifact -eq 'issue-846') { "# $artifact`n`n$(Get-Ledger $false)" } else { "# $artifact`n" }
        Write-TestText (Join-Path $root "$artifact.md") $body
    }
    return $root
}

function Get-TestText {
    param([string] $Path)
    return [IO.File]::ReadAllText($Path)
}

function Set-TestText {
    param([string] $Path, [string] $Text)
    Write-TestText $Path $Text
}

function Replace-Once {
    param([string] $Text, [string] $Old, [string] $New)
    $index = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Test anchor not found: $Old" }
    return $Text.Remove($index, $Old.Length).Insert($index, $New)
}

function Invoke-PacketTest {
    param([string] $PacketDirectory)
    $output = @(& pwsh -NoProfile -File $packetTest -PacketDirectory $PacketDirectory 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
}

$script:Passed = 0
$script:Failed = 0
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-lifecycle-packet-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null

try {
    Invoke-Case 'accepts the exact ordered 19-row ledger and 15-artifact packet' {
        Invoke-PacketTest (New-Packet 'positive')
    }

    Invoke-Case 'rejects a first-row wrong owner even when the expected owner remains on REQ-019' {
        $packet = New-Packet 'first-owner'
        $path = Join-Path $packet 'issue-835.md'
        Set-TestText $path (Replace-Once (Get-TestText $path) '| REQ-001 requirement 1 | #923 |' '| REQ-001 requirement 1 | #999 |')
        Assert-Fails { Invoke-PacketTest $packet } 'REQ-001 must assign #923; found #999'
    }

    Invoke-Case 'rejects a wrong owner on a middle row' {
        $packet = New-Packet 'middle-owner'
        $path = Join-Path $packet 'issue-835.md'
        Set-TestText $path (Replace-Once (Get-TestText $path) '| REQ-010 requirement 10 | #838 |' '| REQ-010 requirement 10 | #999 |')
        Assert-Fails { Invoke-PacketTest $packet } 'REQ-010 must assign #838; found #999'
    }

    Invoke-Case 'rejects a duplicate required row' {
        $packet = New-Packet 'duplicate-row'
        $path = Join-Path $packet 'issue-835.md'
        Set-TestText $path (Replace-Once (Get-TestText $path) '| REQ-002 requirement 2 | #869 |' '| REQ-001 requirement 2 | #869 |')
        Assert-Fails { Invoke-PacketTest $packet } 'row 2 must be REQ-002; found REQ-001'
    }

    Invoke-Case 'rejects reordered required rows' {
        $packet = New-Packet 'reordered-rows'
        $path = Join-Path $packet 'issue-835.md'
        $text = Replace-Once (Get-TestText $path) '| REQ-001 requirement 1 | #923 |' '| temporary requirement 1 | #923 |'
        $text = Replace-Once $text '| REQ-002 requirement 2 | #869 |' '| REQ-001 requirement 2 | #923 |'
        $text = Replace-Once $text '| temporary requirement 1 | #923 |' '| REQ-002 requirement 1 | #869 |'
        Set-TestText $path $text
        Assert-Fails { Invoke-PacketTest $packet } 'row 1 must be REQ-001; found REQ-002'
    }

    Invoke-Case 'rejects a missing required row' {
        $packet = New-Packet 'missing-row'
        $path = Join-Path $packet 'issue-835.md'
        Set-TestText $path (Replace-Once (Get-TestText $path) "| REQ-019 requirement 19 | #923 | companion 19 | audit 19 |`n" '')
        Assert-Fails { Invoke-PacketTest $packet } 'must contain exactly 19 requirement rows; found 18'
    }

    Invoke-Case 'rejects an extra requirement row' {
        $packet = New-Packet 'extra-row'
        $path = Join-Path $packet 'issue-835.md'
        Set-TestText $path (Replace-Once (Get-TestText $path) '<!-- grace:wdu-requirements:end -->' "| REQ-020 requirement 20 | #999 | companion 20 | audit 20 |`n`n<!-- grace:wdu-requirements:end -->")
        Assert-Fails { Invoke-PacketTest $packet } 'must contain exactly 19 requirement rows; found 20'
    }

    Invoke-Case 'rejects malformed row content' {
        $packet = New-Packet 'malformed-row'
        $path = Join-Path $packet 'issue-835.md'
        Set-TestText $path (Replace-Once (Get-TestText $path) '| REQ-001 requirement 1 | #923 | companion 1 | audit 1 |' '| REQ-001 requirement 1 | #923 | companion 1 | audit 1')
        Assert-Fails { Invoke-PacketTest $packet } 'row at line 5 is malformed'
    }

    Invoke-Case 'rejects each of the 38 detailed and compact primary-owner mutations with artifact and row diagnostics' {
        $failures = 0
        foreach ($artifact in @('issue-835', 'issue-846')) {
            $primary = $artifact -eq 'issue-835'
            foreach ($index in 0..18) {
                $packet = New-Packet "$artifact-owner-$index"
                $path = Join-Path $packet "$artifact.md"
                $requirement = 'REQ-{0:d3}' -f ($index + 1)
                $row = if ($primary) { "| $requirement requirement $($index + 1) | $($expectedOwners[$index]) |" } else { "| $requirement | $($expectedOwners[$index]) |" }
                $replacement = if ($primary) { "| $requirement requirement $($index + 1) | #999 |" } else { "| $requirement | #999 |" }
                Set-TestText $path (Replace-Once (Get-TestText $path) $row $replacement)
                try { Invoke-PacketTest $packet }
                catch {
                    $message = $_.Exception.Message
                    Assert-True $message.Contains("$artifact.md", [StringComparison]::Ordinal) "$artifact mutation $requirement scopes its artifact"
                    Assert-True $message.Contains("row $($index + 1) $requirement must assign $($expectedOwners[$index]); found #999", [StringComparison]::Ordinal) "$artifact mutation $requirement scopes its row"
                    $failures++
                    continue
                }
                throw "$artifact mutation $requirement unexpectedly passed"
            }
        }
        Assert-True ($failures -eq 38) 'all 38 primary-owner mutations fail'
    }

    Invoke-Case 'accepts historical stale evidence while retaining an active current 19 claim' {
        $packet = New-Packet 'historical-stale-evidence'
        $path = Join-Path $packet 'issue-835.md'
        $historicalEvidence = @(
            '<!-- grace:wdu-lifecycle-historical-evidence:start -->',
            'Historical evidence: 17 requirements.',
            '<!-- grace:wdu-lifecycle-historical-evidence:end -->'
        ) -join "`n"
        Set-TestText $path (Replace-Once (Get-TestText $path) "$(Get-Ledger $true)" "$historicalEvidence`n`n$(Get-Ledger $true)")
        Invoke-PacketTest $packet
    }

    Invoke-Case 'rejects stale active-epic count claims despite a separate current 19 claim' {
        foreach ($staleClaim in @('17/17 requirements', '17 requirements', '17 total requirements', 'seventeen requirements', 'all 17 active requirements')) {
            $packet = New-Packet ('stale-claim-' + $staleClaim.Replace(' ', '-').Replace('/', '-'))
            $path = Join-Path $packet 'issue-835.md'
            Set-TestText $path ((Get-TestText $path) + "`nCurrent stale claim: $staleClaim.`n")
            Assert-Fails { Invoke-PacketTest $packet } 'active epic must not claim stale 17-requirement completion'
        }
    }

    Invoke-Case 'rejects incomplete, reversed, and duplicate historical evidence markers' {
        $mutations = @(
            @{ Name = 'unclosed'; Text = "<!-- grace:wdu-lifecycle-historical-evidence:start -->`nHistorical 17 requirements."; Reason = 'must contain one complete historical evidence marker pair' },
            @{ Name = 'reversed'; Text = "<!-- grace:wdu-lifecycle-historical-evidence:end -->`n<!-- grace:wdu-lifecycle-historical-evidence:start -->"; Reason = 'historical evidence marker end must follow start' },
            @{ Name = 'duplicate'; Text = "<!-- grace:wdu-lifecycle-historical-evidence:start -->`nHistorical 17 requirements.`n<!-- grace:wdu-lifecycle-historical-evidence:end -->`n<!-- grace:wdu-lifecycle-historical-evidence:start -->`nHistorical 17 requirements.`n<!-- grace:wdu-lifecycle-historical-evidence:end -->"; Reason = 'duplicate historical evidence markers' }
        )
        foreach ($mutation in $mutations) {
            $packet = New-Packet ('historical-marker-' + $mutation.Name)
            $path = Join-Path $packet 'issue-835.md'
            Set-TestText $path ((Get-TestText $path) + "`n$($mutation.Text)`n")
            Assert-Fails { Invoke-PacketTest $packet } $mutation.Reason
        }
    }

    Invoke-Case 'accepts escaped pipes in detailed and compact ordinary ledger cells' {
        foreach ($artifact in @('issue-835', 'issue-846')) {
            $packet = New-Packet ("escaped-pipe-" + $artifact)
            $path = Join-Path $packet "$artifact.md"
            $old = if ($artifact -eq 'issue-835') { '| REQ-001 requirement 1 | #923 | companion 1 | audit 1 |' } else { '| REQ-001 | #923 | audit 1 |' }
            $new = if ($artifact -eq 'issue-835') { '| REQ-001 requirement 1 | #923 | companion 1 \| retained text | audit 1 |' } else { '| REQ-001 | #923 | audit 1 \| retained text |' }
            Set-TestText $path (Replace-Once (Get-TestText $path) $old $new)
            Invoke-PacketTest $packet
        }
    }

    Invoke-Case 'rejects raw extra-column pipes and treats even escaped backslashes as a delimiter' {
        $mutations = @(
            @{ Name = 'detailed raw extra'; Artifact = 'issue-835'; Old = '| REQ-001 requirement 1 | #923 | companion 1 | audit 1 |'; New = '| REQ-001 requirement 1 | #923 | companion 1 | raw extra | audit 1 |' },
            @{ Name = 'compact raw extra'; Artifact = 'issue-846'; Old = '| REQ-001 | #923 | audit 1 |'; New = '| REQ-001 | #923 | audit 1 | raw extra |' },
            @{ Name = 'detailed even backslashes'; Artifact = 'issue-835'; Old = '| REQ-001 requirement 1 | #923 | companion 1 | audit 1 |'; New = '| REQ-001 requirement 1 | #923 | companion 1 \\| raw extra | audit 1 |' }
        )
        foreach ($mutation in $mutations) {
            $packet = New-Packet $mutation.Name.Replace(' ', '-')
            $path = Join-Path $packet "$($mutation.Artifact).md"
            Set-TestText $path (Replace-Once (Get-TestText $path) $mutation.Old $mutation.New)
            Assert-Fails { Invoke-PacketTest $packet } 'is malformed'
        }
    }

    Invoke-Case 'rejects alternate complete tables before and after the detailed epic table' {
        $alternate = (Get-Ledger $false).Replace('<!-- grace:wdu-requirements:start -->', '').Replace('<!-- grace:wdu-requirements:end -->', '').Trim()
        foreach ($position in @('before', 'after')) {
            $packet = New-Packet "alternate-table-$position"
            $path = Join-Path $packet 'issue-835.md'
            $text = Get-TestText $path
            $text = if ($position -eq 'before') {
                Replace-Once $text '<!-- grace:wdu-requirements:start -->' ("<!-- grace:wdu-requirements:start -->`n$alternate")
            }
            else {
                Replace-Once $text '<!-- grace:wdu-requirements:end -->' ("$alternate`n<!-- grace:wdu-requirements:end -->")
            }
            Set-TestText $path $text
            $reason = if ($position -eq 'before') { 'has content before its requirement table' } else { 'row at line' }
            Assert-Fails { Invoke-PacketTest $packet } $reason
        }
    }

    Invoke-Case 'rejects finite-ledger header, divider, column, case, and duplicate-block changes' {
        $mutations = @(
            @{ Name = 'detailed header case'; Artifact = 'issue-835'; Old = '| Requirement |'; New = '| requirement |'; Reason = 'one exact requirement table header' },
            @{ Name = 'compact divider'; Artifact = 'issue-846'; Old = '| --- | --- | --- |'; New = '| --- | --- | ---'; Reason = 'exact requirement table divider' },
            @{ Name = 'detailed wrong columns'; Artifact = 'issue-835'; Old = '| REQ-001 requirement 1 | #923 | companion 1 | audit 1 |'; New = '| REQ-001 requirement 1 | #923 | companion 1 |'; Reason = 'is malformed' },
            @{ Name = 'compact wrong columns'; Artifact = 'issue-846'; Old = '| REQ-001 | #923 | audit 1 |'; New = '| REQ-001 | #923 | audit 1 | extra |'; Reason = 'is malformed' },
            @{ Name = 'compact requirement case'; Artifact = 'issue-846'; Old = '| REQ-001 | #923 |'; New = '| req-001 | #923 |'; Reason = 'is malformed' },
            @{ Name = 'duplicate marker block'; Artifact = 'issue-846'; Old = '<!-- grace:wdu-requirements:end -->'; New = "<!-- grace:wdu-requirements:end -->`n<!-- grace:wdu-requirements:start -->`n<!-- grace:wdu-requirements:end -->"; Reason = 'duplicate requirements ledger markers' }
        )
        foreach ($mutation in $mutations) {
            $packet = New-Packet $mutation.Name.Replace(' ', '-')
            $path = Join-Path $packet "$($mutation.Artifact).md"
            Set-TestText $path (Replace-Once (Get-TestText $path) $mutation.Old $mutation.New)
            Assert-Fails { Invoke-PacketTest $packet } $mutation.Reason
        }
    }

    Invoke-Case 'accepts the finite ledgers with CR-only line endings' {
        $packet = New-Packet 'cr-only-ledgers'
        foreach ($artifact in @('issue-835', 'issue-846')) {
            $path = Join-Path $packet "$artifact.md"
            Set-TestText $path ((Get-TestText $path).Replace("`r`n", "`n").Replace("`n", "`r"))
        }
        Invoke-PacketTest $packet
    }
}
finally {
    if (Test-Path -LiteralPath $script:TestRoot) { Remove-Item -LiteralPath $script:TestRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
