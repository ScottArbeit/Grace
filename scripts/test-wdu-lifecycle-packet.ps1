[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PacketDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredArtifacts = @(
    'adr-0011', 'issue-835', 'issue-842', 'issue-843', 'issue-846', 'issue-869', 'issue-898', 'issue-920',
    'issue-921', 'issue-922', 'issue-923', 'issue-900', 'issue-901', 'issue-871', 'issue-872'
)
$expectedRequirements = 1..19 | ForEach-Object { 'REQ-{0:d3}' -f $_ }
$expectedOwners = [ordered]@{
    'REQ-001' = '#923'; 'REQ-002' = '#869'; 'REQ-003' = '#837'; 'REQ-004' = '#839'; 'REQ-005' = '#869'
    'REQ-006' = '#898'; 'REQ-007' = '#922'; 'REQ-008' = '#922'; 'REQ-009' = '#838'; 'REQ-010' = '#838'
    'REQ-011' = '#871'; 'REQ-012' = '#871'; 'REQ-013' = '#900'; 'REQ-014' = '#921'; 'REQ-015' = '#842'
    'REQ-016' = '#871'; 'REQ-017' = '#846'; 'REQ-018' = '#920'; 'REQ-019' = '#923'
}
$primaryOwnerTableHeader = '| Requirement | Primary delivery issue | Companion proof, extension, or deferred disposition | #846 audit disposition |'
$primaryOwnerTableDivider = '| --- | --- | --- | --- |'
$auditTableHeader = '| Requirement | Primary delivery issue | Audit disposition |'
$auditTableDivider = '| --- | --- | --- |'

function Get-RequirementLedger {
    param([string] $Path)
    $text = [IO.File]::ReadAllText($Path)
    $start = '<!-- grace:wdu-requirements:start -->'
    $end = '<!-- grace:wdu-requirements:end -->'
    $startIndex = $text.IndexOf($start, [StringComparison]::Ordinal)
    $endIndex = $text.IndexOf($end, [StringComparison]::Ordinal)
    if ($startIndex -lt 0 -or $endIndex -le $startIndex) { throw "WDU lifecycle packet '$Path' must contain one complete requirements ledger marker pair" }
    if ($text.IndexOf($start, $startIndex + $start.Length, [StringComparison]::Ordinal) -ge 0 -or $text.IndexOf($end, $endIndex + $end.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "WDU lifecycle packet '$Path' has duplicate requirements ledger markers"
    }
    return $text.Substring($startIndex + $start.Length, $endIndex - ($startIndex + $start.Length))
}

function Get-RequirementRows {
    param([string] $Ledger, [string] $Path, [bool] $RequireOwners)

    $lines = @($Ledger -split "`r`n|`n|`r")
    $tableHeader = if ($RequireOwners) { $primaryOwnerTableHeader } else { $auditTableHeader }
    $tableDivider = if ($RequireOwners) { $primaryOwnerTableDivider } else { $auditTableDivider }
    $columnCount = if ($RequireOwners) { 6 } else { 5 }
    $headerIndexes = @(
        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ([StringComparer]::Ordinal.Equals($lines[$index].Trim(), $tableHeader)) { $index }
        }
    )
    if ($headerIndexes.Count -ne 1) {
        throw "WDU lifecycle packet '$Path' requirements ledger must contain one exact requirement table header"
    }

    $headerIndex = $headerIndexes[0]
    if ($headerIndex + 1 -ge $lines.Count -or -not [StringComparer]::Ordinal.Equals($lines[$headerIndex + 1].Trim(), $tableDivider)) {
        throw "WDU lifecycle packet '$Path' requirements ledger must contain the exact requirement table divider"
    }

    $rows = [Collections.Generic.List[object]]::new()
    for ($index = $headerIndex + 2; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if (-not $line.TrimStart().StartsWith('|', [StringComparison]::Ordinal)) {
            throw "WDU lifecycle packet '$Path' requirements ledger has non-table content after its requirement table at line $($index + 1)"
        }

        $columns = @($line.Split('|'))
        if ($columns.Count -ne $columnCount -or -not [string]::IsNullOrWhiteSpace($columns[0]) -or -not [string]::IsNullOrWhiteSpace($columns[$columnCount - 1])) {
            throw "WDU lifecycle packet '$Path' requirements ledger row at line $($index + 1) is malformed"
        }

        $requirementPattern = if ($RequireOwners) { '\A(?<id>REQ-\d{3})\s+\S.*\z' } else { '\A(?<id>REQ-\d{3})\z' }
        $requirementMatch = [regex]::Match($columns[1].Trim(), $requirementPattern)
        $owner = $columns[2].Trim()
        $requiredContent = if ($RequireOwners) { @($columns[3], $columns[4]) } else { @($columns[3]) }
        if (-not $requirementMatch.Success -or -not [regex]::IsMatch($owner, '\A#\d+\z') -or @($requiredContent | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
            throw "WDU lifecycle packet '$Path' requirements ledger row at line $($index + 1) is malformed"
        }

        $rows.Add([pscustomobject]@{ Id = $requirementMatch.Groups['id'].Value; Owner = $owner; LineNumber = $index + 1 })
    }

    return @($rows)
}

function Assert-RequirementLedger {
    param([string] $Path, [bool] $RequireOwners)
    $ledger = Get-RequirementLedger $Path
    $rows = @(Get-RequirementRows -Ledger $ledger -Path $Path -RequireOwners $RequireOwners)
    if ($rows.Count -ne 19) { throw "WDU lifecycle packet '$Path' requirements ledger must contain exactly 19 requirement rows; found $($rows.Count)" }
    for ($index = 0; $index -lt $expectedRequirements.Count; $index++) {
        $requirement = $expectedRequirements[$index]
        if (-not [StringComparer]::Ordinal.Equals($rows[$index].Id, $requirement)) {
            throw "WDU lifecycle packet '$Path' requirements ledger row $($index + 1) must be $requirement; found $($rows[$index].Id)"
        }
        if ($RequireOwners -and -not [StringComparer]::Ordinal.Equals($rows[$index].Owner, $expectedOwners[$requirement])) {
            throw "WDU lifecycle packet '$Path' requirements ledger row $($index + 1) $requirement must assign $($expectedOwners[$requirement]); found $($rows[$index].Owner)"
        }
    }
}

function Assert-NoOperationalSupersededReference {
    param([string] $Path)
    $text = [IO.File]::ReadAllText($Path)
    $historicalStart = '<!-- grace:wdu-historical:start -->'
    $historicalEnd = '<!-- grace:wdu-historical:end -->'
    $cursor = 0
    while ($true) {
        $reference = $text.IndexOf('#899', $cursor, [StringComparison]::Ordinal)
        if ($reference -lt 0) { return }
        $start = $text.LastIndexOf($historicalStart, $reference, [StringComparison]::Ordinal)
        $end = if ($start -lt 0) { -1 } else { $text.IndexOf($historicalEnd, $start + $historicalStart.Length, [StringComparison]::Ordinal) }
        if ($start -lt 0 -or $end -lt $reference) { throw "WDU lifecycle packet '$Path' has operational #899 text outside a historical marker section" }
        $cursor = $reference + 4
    }
}

$resolved = [IO.Path]::GetFullPath($PacketDirectory)
$files = Get-ChildItem -LiteralPath $resolved -File -Filter '*.md'
if ($files.Count -ne $requiredArtifacts.Count) { throw "WDU lifecycle packet '$resolved' must contain exactly 15 Markdown artifacts" }
foreach ($artifact in $requiredArtifacts) {
    $path = Join-Path $resolved "$artifact.md"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "WDU lifecycle packet '$resolved' is missing $artifact.md" }
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains('`n', [StringComparison]::Ordinal)) { throw "WDU lifecycle packet '$path' contains a literal PowerShell newline escape artifact" }
    Assert-NoOperationalSupersededReference $path
}

Assert-RequirementLedger (Join-Path $resolved 'issue-835.md') $true
Assert-RequirementLedger (Join-Path $resolved 'issue-846.md') $false
Write-Host "PASS WDU lifecycle packet: 19 requirements, 15 artifacts, no operational #899 references, and no literal newline escapes"
