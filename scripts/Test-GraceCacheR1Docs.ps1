[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Get-MarkdownHeading {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]] $Lines,

        [Parameter(Mandatory)]
        [string] $Title,

        [Parameter(Mandatory)]
        [string] $DocumentPath
    )

    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match '^(?<markers>#{1,6}) (?<heading>.+?)\s*$' -and $Matches.heading -eq $Title) {
            return [pscustomobject]@{
                Index = $index
                Level = $Matches.markers.Length
            }
        }
    }

    throw "$DocumentPath does not contain the '$Title' heading."
}

function ConvertFrom-MarkdownTableRow {
    param(
        [Parameter(Mandatory)]
        [string] $Line,

        [Parameter(Mandatory)]
        [string] $DocumentPath
    )

    if ($Line -notmatch '^\|.*\|\s*$') {
        throw "$DocumentPath contains a malformed Markdown table row: $Line"
    }

    return @($Line.Trim().Trim('|').Split('|').ForEach({ $_.Trim() }))
}

function Get-MarkdownTableAfterHeading {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]] $Lines,

        [Parameter(Mandatory)]
        [string] $HeadingTitle,

        [Parameter(Mandatory)]
        [string] $DocumentPath
    )

    $heading = Get-MarkdownHeading -Lines $Lines -Title $HeadingTitle -DocumentPath $DocumentPath
    $sectionEnd = $Lines.Count

    for ($index = $heading.Index + 1; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match '^(?<markers>#{1,6}) ') {
            if ($Matches.markers.Length -le $heading.Level) {
                $sectionEnd = $index
                break
            }
        }
    }

    $tableStart = -1
    for ($index = $heading.Index + 1; $index -lt $sectionEnd - 1; $index++) {
        if ($Lines[$index] -match '^\|.*\|\s*$' -and $Lines[$index + 1] -match '^\|\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|\s*$') {
            $tableStart = $index
            break
        }
    }

    if ($tableStart -lt 0) {
        throw "$DocumentPath does not contain a Markdown table directly under '$HeadingTitle'."
    }

    $headers = ConvertFrom-MarkdownTableRow -Line $Lines[$tableStart] -DocumentPath $DocumentPath
    $rows = @()

    for ($index = $tableStart + 2; $index -lt $sectionEnd; $index++) {
        if ($Lines[$index] -notmatch '^\|.*\|\s*$') {
            break
        }

        $cells = ConvertFrom-MarkdownTableRow -Line $Lines[$index] -DocumentPath $DocumentPath
        if ($cells.Count -ne $headers.Count) {
            throw "$DocumentPath table under '$HeadingTitle' has $($cells.Count) cells where $($headers.Count) are required."
        }

        $row = [ordered]@{}
        for ($cellIndex = 0; $cellIndex -lt $headers.Count; $cellIndex++) {
            $row[$headers[$cellIndex]] = $cells[$cellIndex]
        }

        $rows += [pscustomobject]$row
    }

    return [pscustomobject]@{
        Headers = $headers
        Rows = $rows
    }
}

function Get-MarkdownSectionText {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]] $Lines,

        [Parameter(Mandatory)]
        [string] $HeadingTitle,

        [Parameter(Mandatory)]
        [string] $DocumentPath
    )

    $heading = Get-MarkdownHeading -Lines $Lines -Title $HeadingTitle -DocumentPath $DocumentPath
    $sectionLines = @()

    for ($index = $heading.Index + 1; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match '^(?<markers>#{1,6}) ' -and $Matches.markers.Length -le $heading.Level) {
            break
        }

        $sectionLines += $Lines[$index]
    }

    return $sectionLines -join "`n"
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)]
        [string] $Text,

        [Parameter(Mandatory)]
        [string] $Expected,

        [Parameter(Mandatory)]
        [string] $Description
    )

    $normalizedText = $Text -replace '\s+', ' '
    $normalizedExpected = $Expected -replace '\s+', ' '

    if (-not $normalizedText.Contains($normalizedExpected, [System.StringComparison]::Ordinal)) {
        throw "$Description must contain '$Expected'."
    }
}

function Assert-DoesNotMatch {
    param(
        [Parameter(Mandatory)]
        [string] $Text,

        [Parameter(Mandatory)]
        [string] $Pattern,

        [Parameter(Mandatory)]
        [string] $Description
    )

    if ($Text -match $Pattern) {
        throw "$Description still matches obsolete text '$Pattern'."
    }
}

$expectedRows = [ordered]@{
    '#886' = 'Implemented and proven; completed evidence.'
    'PR #888' = 'Merged implementation and proof for #886.'
    '#887' = 'Superseded mixed enrollment/status issue.'
    'PR #896' = 'Closed superseded mixed enrollment/status implementation.'
    '#904' = 'Superseded status issue.'
    'PR #907' = 'Closed superseded status implementation.'
    '#913 / #914' = 'Current docs-only correction; planned pure local status follows.'
    '#905' = 'Planned one-shot enrollment after #913 and #914.'
}

$requiredDisposition = @(
    'An inactive accepted server enrollment is unselectable.',
    'Fresh manual enrollment is allowed.',
    'Server expiry performs eventual cleanup.',
    'Grace Cache adds no automatic enrollment retry or reconciliation.'
)

$obsoletePatterns = @(
    'R1 ambiguity gate',
    'Inactive-orphan proof before R1A code',
    'R1 may accept an inactive orphan only after',
    'Before this path is implemented, R1 must prove',
    'Before code, prove the inactive-orphan condition',
    'R1''s current-contract proof',
    'current implementation leaf',
    'R1B follows R1A',
    '\bR1B\b',
    'R1A validation status',
    'remain required before merge'
)

$documents = @(
    (Join-Path $repositoryRoot 'docs/Grace Cache.md')
    (Join-Path $repositoryRoot 'docs/Grace Cache implementation audit.md')
)

foreach ($documentPath in $documents) {
    $lines = Get-Content -LiteralPath $documentPath
    $trackerTable = Get-MarkdownTableAfterHeading -Lines $lines -HeadingTitle 'Current R1 tracker record' -DocumentPath $documentPath

    if ($trackerTable.Headers.Count -ne 3 -or $trackerTable.Headers[0] -ne 'Tracker' -or $trackerTable.Headers[1] -ne 'Current classification' -or $trackerTable.Headers[2] -ne 'Scope and sequence') {
        throw "$documentPath must use the Tracker, Current classification, Scope and sequence table contract."
    }

    if ($trackerTable.Rows.Count -ne $expectedRows.Count) {
        throw "$documentPath must classify exactly $($expectedRows.Count) current R1 tracker rows."
    }

    $actualRows = @{}
    foreach ($row in $trackerTable.Rows) {
        if ($actualRows.ContainsKey($row.Tracker)) {
            throw "$documentPath repeats tracker row '$($row.Tracker)'."
        }

        $actualRows[$row.Tracker] = $row
    }

    foreach ($expectedRow in $expectedRows.GetEnumerator()) {
        if (-not $actualRows.ContainsKey($expectedRow.Key)) {
            throw "$documentPath does not classify tracker row '$($expectedRow.Key)'."
        }

        if ($actualRows[$expectedRow.Key].'Current classification' -cne $expectedRow.Value) {
            throw "$documentPath classifies '$($expectedRow.Key)' as '$($actualRows[$expectedRow.Key].'Current classification')', not '$($expectedRow.Value)'."
        }
    }

    $statusScope = $actualRows['#913 / #914'].'Scope and sequence'
    Assert-Contains -Text $statusScope -Expected '#914 then owns status only.' -Description "$documentPath #914 scope"
    Assert-DoesNotMatch -Text $statusScope -Pattern 'enrollment' -Description "$documentPath #914 scope"

    $enrollmentScope = $actualRows['#905'].'Scope and sequence'
    Assert-Contains -Text $enrollmentScope -Expected 'enrollment only' -Description "$documentPath #905 scope"
    Assert-DoesNotMatch -Text $enrollmentScope -Pattern 'status only' -Description "$documentPath #905 scope"

    $disposition = Get-MarkdownSectionText -Lines $lines -HeadingTitle 'Enrollment ambiguity disposition' -DocumentPath $documentPath
    foreach ($statement in $requiredDisposition) {
        Assert-Contains -Text $disposition -Expected $statement -Description "$documentPath enrollment ambiguity disposition"
    }

    $documentText = $lines -join "`n"
    foreach ($pattern in $obsoletePatterns) {
        Assert-DoesNotMatch -Text $documentText -Pattern $pattern -Description $documentPath
    }
}

Write-Output 'Grace Cache R1 documentation record is current.'
