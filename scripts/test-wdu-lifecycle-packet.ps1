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
$detailedEpicTable = [pscustomobject]@{
    Header = '| Requirement | Primary delivery issue | Companion proof, extension, or deferred disposition | #846 audit disposition |'
    Divider = '| --- | --- | --- | --- |'
    ColumnCount = 6
}
$compactAuditTable = [pscustomobject]@{
    Header = '| Requirement | Primary delivery issue | Audit disposition |'
    Divider = '| --- | --- | --- |'
    ColumnCount = 5
}

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
    param([string] $Ledger, [string] $Path, [object] $TableShape)

    $lines = @($Ledger -split "`r`n|`n|`r")
    $headerIndexes = @(
        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ([StringComparer]::Ordinal.Equals($lines[$index].Trim(), $TableShape.Header)) { $index }
        }
    )
    if ($headerIndexes.Count -ne 1) {
        throw "WDU lifecycle packet '$Path' requirements ledger must contain one exact requirement table header"
    }

    $headerIndex = $headerIndexes[0]
    for ($index = 0; $index -lt $headerIndex; $index++) {
        if (-not [string]::IsNullOrWhiteSpace($lines[$index])) {
            throw "WDU lifecycle packet '$Path' requirements ledger has content before its requirement table at line $($index + 1)"
        }
    }
    if ($headerIndex + 1 -ge $lines.Count -or -not [StringComparer]::Ordinal.Equals($lines[$headerIndex + 1].Trim(), $TableShape.Divider)) {
        throw "WDU lifecycle packet '$Path' requirements ledger must contain the exact requirement table divider"
    }

    $rows = [Collections.Generic.List[object]]::new()
    for ($index = $headerIndex + 2; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if (-not $line.TrimStart().StartsWith('|', [StringComparison]::Ordinal)) {
            throw "WDU lifecycle packet '$Path' requirements ledger has non-table content after its requirement table at line $($index + 1)"
        }

        $columns = @(Split-RequirementTableRow $line)
        if ($columns.Count -ne $TableShape.ColumnCount -or -not [string]::IsNullOrWhiteSpace($columns[0]) -or -not [string]::IsNullOrWhiteSpace($columns[$TableShape.ColumnCount - 1])) {
            throw "WDU lifecycle packet '$Path' requirements ledger row at line $($index + 1) is malformed"
        }

        $requirementPattern = if ($TableShape -eq $detailedEpicTable) { '\A(?<id>REQ-\d{3})\s+\S.*\z' } else { '\A(?<id>REQ-\d{3})\z' }
        $requirementMatch = [regex]::Match($columns[1].Trim(), $requirementPattern)
        $owner = $columns[2].Trim()
        $requiredContent = if ($TableShape -eq $detailedEpicTable) { @($columns[3], $columns[4]) } else { @($columns[3]) }
        if (-not $requirementMatch.Success -or -not [regex]::IsMatch($owner, '\A#\d+\z') -or @($requiredContent | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
            throw "WDU lifecycle packet '$Path' requirements ledger row at line $($index + 1) is malformed"
        }

        $rows.Add([pscustomobject]@{ Id = $requirementMatch.Groups['id'].Value; Owner = $owner; LineNumber = $index + 1 })
    }

    return @($rows)
}

function Split-RequirementTableRow {
    param([string] $Row)

    $columns = [Collections.Generic.List[string]]::new()
    $cell = [Text.StringBuilder]::new()
    $backslashCount = 0
    foreach ($character in $Row.ToCharArray()) {
        if ($character -eq '\') {
            [void] $cell.Append($character)
            $backslashCount++
            continue
        }

        if ($character -eq '|') {
            if ($backslashCount % 2 -eq 1) {
                [void] $cell.Remove($cell.Length - 1, 1)
                [void] $cell.Append($character)
            }
            else {
                $columns.Add($cell.ToString())
                [void] $cell.Clear()
            }
            $backslashCount = 0
            continue
        }

        [void] $cell.Append($character)
        $backslashCount = 0
    }

    $columns.Add($cell.ToString())
    return @($columns.ToArray())
}

function Assert-RequirementLedger {
    param([string] $Path, [object] $TableShape)
    $ledger = Get-RequirementLedger $Path
    $rows = @(Get-RequirementRows -Ledger $ledger -Path $Path -TableShape $TableShape)
    if ($rows.Count -ne 19) { throw "WDU lifecycle packet '$Path' requirements ledger must contain exactly 19 requirement rows; found $($rows.Count)" }
    for ($index = 0; $index -lt $expectedRequirements.Count; $index++) {
        $requirement = $expectedRequirements[$index]
        if (-not [StringComparer]::Ordinal.Equals($rows[$index].Id, $requirement)) {
            throw "WDU lifecycle packet '$Path' requirements ledger row $($index + 1) must be $requirement; found $($rows[$index].Id)"
        }
        if (-not [StringComparer]::Ordinal.Equals($rows[$index].Owner, $expectedOwners[$requirement])) {
            throw "WDU lifecycle packet '$Path' requirements ledger row $($index + 1) $requirement must assign $($expectedOwners[$requirement]); found $($rows[$index].Owner)"
        }
    }
}

function Assert-ActiveEpicRequirementCount {
    param([string] $Path)
    $text = [IO.File]::ReadAllText($Path)
    $activeText = Get-ActiveEpicText $text $Path
    $staleClaim = [regex]::Match($activeText, '\b(?:17\s*/\s*17|(?:(?:all|exactly|current)\s+)?17\s+(?:(?:total|active)\s+)?requirements?|(?:(?:all|exactly|current)\s+)?seventeen\s+(?:(?:total|active)\s+)?requirements?)\b', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($staleClaim.Success) {
        throw "WDU lifecycle packet '$Path' active epic must not claim stale 17-requirement completion: '$($staleClaim.Value)'"
    }
    if (-not [regex]::IsMatch($activeText, '\b(?:19\s*/\s*19|(?:(?:all|exactly|current)\s+)?19\s+(?:(?:total|active)\s+)?requirements?)\b', [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        throw "WDU lifecycle packet '$Path' active epic must state 19-requirement completion"
    }
}

function Get-ActiveEpicText {
    param([string] $Text, [string] $Path)

    $historicalStart = '<!-- grace:wdu-lifecycle-historical-evidence:start -->'
    $historicalEnd = '<!-- grace:wdu-lifecycle-historical-evidence:end -->'
    $startIndexes = [Collections.Generic.List[int]]::new()
    $endIndexes = [Collections.Generic.List[int]]::new()
    $cursor = 0
    while ($true) {
        $index = $Text.IndexOf($historicalStart, $cursor, [StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $startIndexes.Add($index)
        $cursor = $index + $historicalStart.Length
    }
    $cursor = 0
    while ($true) {
        $index = $Text.IndexOf($historicalEnd, $cursor, [StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $endIndexes.Add($index)
        $cursor = $index + $historicalEnd.Length
    }

    if ($startIndexes.Count -eq 0 -and $endIndexes.Count -eq 0) { return $Text }
    if ($startIndexes.Count -gt 1 -or $endIndexes.Count -gt 1) {
        throw "WDU lifecycle packet '$Path' has duplicate historical evidence markers"
    }
    if ($startIndexes.Count -ne 1 -or $endIndexes.Count -ne 1) {
        throw "WDU lifecycle packet '$Path' must contain one complete historical evidence marker pair"
    }
    if ($endIndexes[0] -le $startIndexes[0]) {
        throw "WDU lifecycle packet '$Path' historical evidence marker end must follow start"
    }

    return $Text.Remove($startIndexes[0], $endIndexes[0] + $historicalEnd.Length - $startIndexes[0])
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

Assert-ActiveEpicRequirementCount (Join-Path $resolved 'issue-835.md')
Assert-RequirementLedger (Join-Path $resolved 'issue-835.md') $detailedEpicTable
Assert-RequirementLedger (Join-Path $resolved 'issue-846.md') $compactAuditTable
Write-Host "PASS WDU lifecycle packet: 19 requirements, 15 artifacts, exact epic and audit ledgers, no stale epic count, no operational #899 references, and no literal newline escapes"
