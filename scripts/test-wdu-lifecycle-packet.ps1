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

function Assert-RequirementLedger {
    param([string] $Path, [bool] $RequireOwners)
    $ledger = Get-RequirementLedger $Path
    $matches = @([regex]::Matches($ledger, 'REQ-\d{3}'))
    if ($matches.Count -ne 19) { throw "WDU lifecycle packet '$Path' requirements ledger must contain exactly 19 requirement IDs" }
    for ($index = 0; $index -lt $expectedRequirements.Count; $index++) {
        if (-not [StringComparer]::Ordinal.Equals($matches[$index].Value, $expectedRequirements[$index])) {
            throw "WDU lifecycle packet '$Path' requirements ledger must list $($expectedRequirements[$index]) at ordinal $($index + 1)"
        }
    }
    if ($RequireOwners) {
        foreach ($requirement in $expectedOwners.Keys) {
            $pattern = [regex]::Escape($requirement) + '.*?\|\s*' + [regex]::Escape($expectedOwners[$requirement]) + '\s*\|'
            if (-not [regex]::IsMatch($ledger, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
                throw "WDU lifecycle packet '$Path' must assign $requirement to $($expectedOwners[$requirement])"
            }
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
