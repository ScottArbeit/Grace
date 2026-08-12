# Copyright (c) Scott Arbeit.
# Licensed under the MIT License.

<#
.SYNOPSIS
Validates the canonical Working Directory Update lifecycle and its marker-delimited projections.

.DESCRIPTION
Runs offline and read-only by default. Pass RenderOutputPath to write corrected projection markers to a separate output
path; input artifacts are never rewritten.

.EXAMPLE
pwsh ./scripts/validate-wdu-lifecycle-contract.ps1 -CanonicalPath './docs/Working Directory Update.md' `
    -ProjectionPath './docs/adr/0011-working-directory-update-transaction.md' `
    -OfflineIssueBodyPath './exports/epic-835.md', './exports/issue-842.md'
#>

[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory)]
    [string] $CanonicalPath,

    [string[]] $ProjectionPath = @(),

    [string[]] $OfflineIssueBodyPath = @(),

    [Parameter(Mandatory, ParameterSetName = 'Render')]
    [string] $RenderOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProjectionPlanStart = '<!-- grace:wdu-lifecycle-projection-plan:start -->'
$script:ProjectionPlanEnd = '<!-- grace:wdu-lifecycle-projection-plan:end -->'
$script:ProjectionStart = '<!-- grace:wdu-lifecycle-projection:start -->'
$script:ProjectionEnd = '<!-- grace:wdu-lifecycle-projection:end -->'

function Fail-Contract([string] $artifact, [string] $reason) {
    throw "WDU lifecycle validation failed [$artifact]: $reason"
}

function Read-Utf8Text([string] $path) {
    $resolved = (Resolve-Path -LiteralPath $path -ErrorAction Stop).Path
    return [pscustomobject]@{ Path = $resolved; Text = [IO.File]::ReadAllText($resolved) }
}

function Get-SingleMarkedBlock(
    [string] $text,
    [string] $startMarker,
    [string] $endMarker,
    [string] $artifact
) {
    $startMatches = [regex]::Matches($text, [regex]::Escape($startMarker))
    $endMatches = [regex]::Matches($text, [regex]::Escape($endMarker))
    if ($startMatches.Count -ne 1 -or $endMatches.Count -ne 1) {
        Fail-Contract $artifact "expected exactly one matched '$startMarker'/'$endMarker' block"
    }
    $start = $startMatches[0].Index
    $contentStart = $start + $startMatches[0].Length
    $end = $endMatches[0].Index
    if ($end -le $contentStart) { Fail-Contract $artifact 'projection markers are unmatched or reversed' }
    return [pscustomobject]@{
        Start = $start
        ContentStart = $contentStart
        End = $end
        EndAfter = $end + $endMatches[0].Length
        Content = $text.Substring($contentStart, $end - $contentStart)
    }
}

function ConvertFrom-FencedJson([string] $text, [string] $artifact, [string] $schema) {
    $matches = [regex]::Matches($text, '(?s)```json\s*(\{.*?\})\s*```')
    $matching = @()
    foreach ($match in $matches) {
        try { $value = $match.Groups[1].Value | ConvertFrom-Json -Depth 100 } catch { continue }
        if ($null -ne $value.PSObject.Properties['schema'] -and $value.schema -eq $schema) { $matching += $value }
    }
    if ($matching.Count -ne 1) { Fail-Contract $artifact "expected exactly one '$schema' JSON block" }
    return $matching[0]
}

function Get-PropertyNames($value) {
    return @($value.PSObject.Properties.Name | Sort-Object)
}

function Assert-ExactProperties($value, [string[]] $names, [string] $artifact) {
    $actual = Get-PropertyNames $value
    if (Compare-Object ($names | Sort-Object) $actual) {
        Fail-Contract $artifact "malformed predicate properties: $($actual -join ', ')"
    }
}

function Assert-ExactMembers($actual, [string[]] $expected, [string] $artifact, [string] $label) {
    $actualMembers = @($actual)
    $difference = @(Compare-Object ($expected | Sort-Object) ($actualMembers | Sort-Object))
    if ($actualMembers.Count -ne $expected.Count -or $difference.Count -ne 0) {
        Fail-Contract $artifact "$label is not the exact canonical member set"
    }
}

function Expand-Predicate($grammar, [string] $axis, $expression, [string] $rowId) {
    if ($null -eq $expression.kind) { Fail-Contract $rowId "$axis predicate has no kind" }
    $concrete = @($grammar.concreteEnums.$axis)
    switch ($expression.kind) {
        'one' {
            Assert-ExactProperties $expression @('kind', 'value') "$rowId/$axis"
            if ($expression.value -notin $concrete) { Fail-Contract $rowId "$axis has unknown value '$($expression.value)'" }
            return @($expression.value)
        }
        'set' {
            Assert-ExactProperties $expression @('kind', 'values') "$rowId/$axis"
            if ($expression.values -is [string] -or $null -eq $expression.values) {
                Fail-Contract $rowId "$axis set must be an array"
            }
            $values = @($expression.values)
            if ($values.Count -eq 0) { Fail-Contract $rowId "$axis set is empty" }
            if (@($values | Group-Object | Where-Object Count -ne 1).Count -ne 0) {
                Fail-Contract $rowId "$axis set contains duplicates"
            }
            $unknown = @($values | Where-Object { $_ -notin $concrete })
            if ($unknown.Count -ne 0) { Fail-Contract $rowId "$axis set contains unknown value '$($unknown[0])'" }
            return $values
        }
        'aggregate' {
            Assert-ExactProperties $expression @('kind', 'name') "$rowId/$axis"
            $axisAggregates = $grammar.aggregates.$axis
            if ($null -eq $axisAggregates -or $null -eq $axisAggregates.PSObject.Properties[$expression.name]) {
                Fail-Contract $rowId "$axis has unknown aggregate '$($expression.name)'"
            }
            $values = @($axisAggregates.($expression.name))
            if ($values.Count -eq 0 -or @($values | Where-Object { $_ -notin $concrete }).Count -ne 0) {
                Fail-Contract $rowId "$axis aggregate '$($expression.name)' has invalid concrete expansion"
            }
            return $values
        }
        default { Fail-Contract $rowId "$axis has unknown predicate kind '$($expression.kind)'" }
    }
}

function Test-LifecycleContract($contract, [string] $artifact) {
    if ($contract.schema -ne 'grace.wdu.branch-lifecycle/v1') { Fail-Contract $artifact 'unexpected lifecycle schema' }
    $grammar = $contract.machineGrammar
    if ($null -eq $grammar) { Fail-Contract $artifact 'machineGrammar is missing' }
    $axes = @('invocation', 'trigger', 'marker', 'selectionState')
    Assert-ExactProperties $grammar @('aggregates', 'concreteEnums', 'encoding', 'expansion', 'overlap', 'predicateAxes',
        'terminalReplay') $artifact
    if (Compare-Object $axes @($grammar.predicateAxes)) { Fail-Contract $artifact 'predicateAxes are not canonical' }
    if (Compare-Object @('aggregate', 'one', 'set') @(Get-PropertyNames $grammar.encoding)) {
        Fail-Contract $artifact 'predicate encodings are not canonical'
    }

    $expectedEnums = [ordered]@{
        invocation = @('initial', 'terminalReplay', 'finalizationRetry')
        trigger = @(
            'afterSqliteLocalCompletion', 'branchPublicationFails', 'branchPublicationFailsAfterExactCleanup',
            'cancelAfterBranchPublicationBegins', 'cancelAfterExactCleanupBegins',
            'cancelAfterFirstWorkingTreeMutation', 'cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation',
            'cancelAfterTerminalRecordingBegins', 'cancelBeforeFirstWorkingTreeMutation',
            'cancelImmediatelyBeforeFirstApplicableRetryWrite', 'disallowedMarker',
            'exactCleanupAndTerminalSucceedAfterCurrentBranchProof',
            'exactCleanupAndTerminalSucceedAfterSelectedBranchProof',
            'exactCleanupFails', 'exactCleanupPublicationAndTerminalSucceed', 'exactSameOperationAdoption',
            'exactTerminalCompletionRegardlessOfInvocationCancellation',
            'failureAfterFirstWorkingTreeMutationBeforeSqliteLocalCompletion',
            'failureBeforeFirstWorkingTreeMutation', 'finalPreMutationRereadCleanupFails',
            'finalPreMutationRereadMatches', 'finalPreMutationRereadRejects', 'firstWorkingTreeMutationBegins',
            'missingMarkerFreshAdmission', 'ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation',
            'preLocalAdmissionRefused', 'publicationAndTerminalSucceed',
            'terminalRecordingFailsAfterExactCleanupAndCurrentBranchProof',
            'terminalRecordingFailsAfterExactCleanupAndPublicationProof',
            'terminalRecordingFailsAfterExactCleanupAndSelectedBranchProof',
            'terminalRecordingFailsAfterPublicationProof', 'terminalRecordingSucceedsAfterPublicationProof',
            'thirdBranchBlocksAfterExactCleanup', 'thirdBranchBlocksFinalization',
            'verifiedRootReadyForSqliteLocalCompletion')
        marker = @('notApplicable', 'missing', 'exact', 'differentOperation', 'malformed', 'unsupported', 'unreadable',
            'exactCleanupFailed')
        selectionState = @('referencePrevious', 'referenceSelected', 'referenceThird', 'directoryVersion')
        firstApplicableRetryWrite = @('none', 'exactCleanup', 'branchPublication', 'terminalRecording')
        exitClass = @('success', 'nonzero')
    }
    Assert-ExactProperties $grammar.concreteEnums @($expectedEnums.Keys) $artifact
    foreach ($enumName in $expectedEnums.Keys) {
        Assert-ExactMembers $grammar.concreteEnums.$enumName $expectedEnums[$enumName] $artifact "concrete enum '$enumName'"
    }

    Assert-ExactProperties $grammar.aggregates @('marker', 'selectionState') $artifact
    $expectedMarkerAggregates = [ordered]@{
        none = @('notApplicable')
        any = @('notApplicable', 'missing', 'exact', 'differentOperation', 'malformed', 'unsupported', 'unreadable',
            'exactCleanupFailed')
        ownedOrNone = @('notApplicable', 'missing', 'exact')
        actualEvidence = @('missing', 'exact', 'differentOperation', 'malformed', 'unsupported', 'unreadable',
            'exactCleanupFailed')
        postCompletionEvidence = @('missing', 'exact', 'differentOperation', 'malformed', 'unsupported', 'unreadable',
            'exactCleanupFailed')
    }
    $expectedSelectionAggregates = [ordered]@{
        any = @('referencePrevious', 'referenceSelected', 'referenceThird', 'directoryVersion')
        persisted = @('referencePrevious', 'referenceSelected', 'referenceThird', 'directoryVersion')
    }
    Assert-ExactProperties $grammar.aggregates.marker @($expectedMarkerAggregates.Keys) $artifact
    Assert-ExactProperties $grammar.aggregates.selectionState @($expectedSelectionAggregates.Keys) $artifact
    foreach ($name in $expectedMarkerAggregates.Keys) {
        Assert-ExactMembers $grammar.aggregates.marker.$name $expectedMarkerAggregates[$name] $artifact "marker aggregate '$name'"
    }
    foreach ($name in $expectedSelectionAggregates.Keys) {
        Assert-ExactMembers $grammar.aggregates.selectionState.$name $expectedSelectionAggregates[$name] $artifact `
            "selectionState aggregate '$name'"
    }

    $rows = @($contract.rows)
    if ($rows.Count -ne 67) { Fail-Contract $artifact "expected 67 rows, found $($rows.Count)" }
    if (@($rows | Where-Object { $null -eq $_.PSObject.Properties['id'] }).Count -ne 0) {
        Fail-Contract $artifact 'row is missing id'
    }
    $duplicates = @($rows | Group-Object id | Where-Object Count -ne 1)
    if ($duplicates.Count -ne 0) { Fail-Contract $artifact "duplicate row '$($duplicates[0].Name)'" }
    if (@($rows | Where-Object id -notmatch '^WDU-LC-\d{3}$').Count -ne 0) {
        Fail-Contract $artifact 'row ID does not match WDU-LC-NNN'
    }
    $requiredRowIds = @(
        'WDU-LC-200', 'WDU-LC-201', 'WDU-LC-202', 'WDU-LC-203', 'WDU-LC-204', 'WDU-LC-205', 'WDU-LC-206',
        'WDU-LC-207', 'WDU-LC-208', 'WDU-LC-209', 'WDU-LC-210', 'WDU-LC-211', 'WDU-LC-212', 'WDU-LC-001',
        'WDU-LC-005', 'WDU-LC-002', 'WDU-LC-004', 'WDU-LC-006', 'WDU-LC-003', 'WDU-LC-010', 'WDU-LC-011',
        'WDU-LC-012', 'WDU-LC-013', 'WDU-LC-014', 'WDU-LC-015', 'WDU-LC-020', 'WDU-LC-021', 'WDU-LC-022',
        'WDU-LC-023', 'WDU-LC-024', 'WDU-LC-025', 'WDU-LC-026', 'WDU-LC-027', 'WDU-LC-030', 'WDU-LC-031',
        'WDU-LC-032', 'WDU-LC-033', 'WDU-LC-034', 'WDU-LC-035', 'WDU-LC-036', 'WDU-LC-037', 'WDU-LC-100',
        'WDU-LC-101', 'WDU-LC-102', 'WDU-LC-103', 'WDU-LC-104', 'WDU-LC-105', 'WDU-LC-106', 'WDU-LC-107',
        'WDU-LC-108', 'WDU-LC-109', 'WDU-LC-115', 'WDU-LC-116', 'WDU-LC-110', 'WDU-LC-111', 'WDU-LC-112',
        'WDU-LC-113', 'WDU-LC-114', 'WDU-LC-120', 'WDU-LC-121', 'WDU-LC-122', 'WDU-LC-123', 'WDU-LC-130',
        'WDU-LC-140', 'WDU-LC-141', 'WDU-LC-142', 'WDU-LC-143')
    Assert-ExactMembers @($rows.id) $requiredRowIds $artifact 'lifecycle row IDs'

    $cells = [Collections.Generic.Dictionary[string, string]]::new()
    $rowCellCounts = @{}
    foreach ($row in $rows) {
        Assert-ExactProperties $row.match $axes "$($row.id)/match"
        $expanded = @{}
        foreach ($axis in $axes) { $expanded[$axis] = @(Expand-Predicate $grammar $axis $row.match.$axis $row.id) }
        $count = 0
        foreach ($invocation in $expanded.invocation) {
            foreach ($trigger in $expanded.trigger) {
                foreach ($marker in $expanded.marker) {
                    foreach ($selection in $expanded.selectionState) {
                        $key = "$invocation|$trigger|$marker|$selection"
                        if ($cells.ContainsKey($key)) {
                            Fail-Contract $artifact "overlapping applicability '$key' in $($cells[$key]) and $($row.id)"
                        }
                        $cells[$key] = $row.id
                        $count++
                    }
                }
            }
        }
        $rowCellCounts[$row.id] = $count
        if ($row.firstApplicableRetryWrite -notin @($grammar.concreteEnums.firstApplicableRetryWrite)) {
            Fail-Contract $row.id "unknown firstApplicableRetryWrite '$($row.firstApplicableRetryWrite)'"
        }
        if ($null -eq $row.outcome) {
            if ($null -eq $row.PSObject.Properties['nextRows'] -or @($row.nextRows).Count -eq 0) {
                Fail-Contract $row.id 'routing row has no nextRows'
            }
        } elseif ($row.outcome -eq 'FinalizationIncomplete' -and
            ($row.exitClass -ne 'nonzero' -or $row.doctorGuidance -ne $true)) {
            Fail-Contract $row.id 'FinalizationIncomplete lacks nonzero exit or Doctor guidance'
        }
    }
    if ($cells.Count -ne 254) { Fail-Contract $artifact "expected 254 expanded applicability keys, found $($cells.Count)" }
    if ($rowCellCounts['WDU-LC-100'] -ne 20) { Fail-Contract 'WDU-LC-100' 'expected 5x4=20 expansion' }
    if ($rowCellCounts['WDU-LC-003'] -ne 32) { Fail-Contract 'WDU-LC-003' 'expected 8x4=32 replay expansion' }

    foreach ($row in $rows) {
        if ($null -ne $row.PSObject.Properties['nextRows']) {
            foreach ($nextId in @($row.nextRows)) {
                if (@($rows | Where-Object id -eq $nextId).Count -ne 1) {
                    Fail-Contract $row.id "unknown next row '$nextId'"
                }
            }
        }
    }

    $disallowed = @('differentOperation', 'malformed', 'unsupported', 'unreadable', 'exactCleanupFailed')
    foreach ($row in $rows) {
        $markers = @(Expand-Predicate $grammar marker $row.match.marker $row.id)
        if (@($markers | Where-Object { $_ -in $disallowed }).Count -gt 0 -and
            $row.requiredActions -contains 'publishSelectedBranch') {
            Fail-Contract $row.id 'disallowed marker evidence advances Branch publication'
        }
    }

    foreach ($row in @($rows | Where-Object {
                $_.requiredActions -contains 'recordTerminal' -or $_.requiredActions -contains 'attemptTerminalRecording' })) {
        $actions = [object[]]$row.requiredActions
        $terminalAction = if ($actions -contains 'recordTerminal') { 'recordTerminal' } else { 'attemptTerminalRecording' }
        $terminal = [array]::IndexOf($actions, $terminalAction)
        $selections = @(Expand-Predicate $grammar selectionState $row.match.selectionState $row.id)
        if ($selections.Count -eq 1) {
            $proofAction = switch ($selections[0]) {
                'referencePrevious' { 'provePublication' }
                'referenceSelected' { 'proveSelectedBranch' }
                'directoryVersion' { 'proveCurrentBranchUnchanged' }
                default { $null }
            }
            if ($null -ne $proofAction) {
                $proof = [array]::IndexOf($actions, $proofAction)
                if ($proof -lt 0 -or $proof -ge $terminal) { Fail-Contract $row.id 'Branch proof does not precede terminal recording' }
            }
        }
        $markers = @(Expand-Predicate $grammar marker $row.match.marker $row.id)
        if ($markers.Count -eq 1 -and $markers[0] -eq 'exact') {
            $cleanup = [array]::IndexOf($actions, 'cleanExactMarker')
            if ($cleanup -lt 0 -or $cleanup -ge $terminal) { Fail-Contract $row.id 'exact cleanup does not precede terminal recording' }
            if ($actions -contains 'publishSelectedBranch' -and
                $cleanup -ge [array]::IndexOf($actions, 'publishSelectedBranch')) {
                Fail-Contract $row.id 'Branch publication precedes exact cleanup'
            }
        }
    }

    foreach ($row in $rows) {
        $markers = @(Expand-Predicate $grammar marker $row.match.marker $row.id)
        if ($markers.Count -eq 1 -and $markers[0] -eq 'exact' -and
            ($row.requiredActions -contains 'publishSelectedBranch' -or
                $row.requiredActions -contains 'attemptPublishSelectedBranch')) {
            $actions = [object[]]$row.requiredActions
            $cleanup = [array]::IndexOf($actions, 'cleanExactMarker')
            $publicationAction = if ($actions -contains 'publishSelectedBranch') {
                'publishSelectedBranch'
            } else {
                'attemptPublishSelectedBranch'
            }
            $publication = [array]::IndexOf($actions, $publicationAction)
            if ($cleanup -lt 0 -or $cleanup -ge $publication) {
                Fail-Contract $row.id 'Branch publication or its attempt precedes exact cleanup'
            }
        }
    }

    $retryRules = @(
        @{ Marker = 'exact'; Selection = '*'; Write = 'exactCleanup' },
        @{ Marker = 'missing'; Selection = 'referencePrevious'; Write = 'branchPublication' },
        @{ Marker = 'missing'; Selection = 'referenceSelected'; Write = 'terminalRecording' },
        @{ Marker = 'missing'; Selection = 'referenceThird'; Write = 'none' },
        @{ Marker = 'missing'; Selection = 'directoryVersion'; Write = 'terminalRecording' }
    )
    foreach ($rule in $retryRules) {
        $matching = @($rows | Where-Object {
                $_.match.invocation.kind -eq 'one' -and $_.match.invocation.value -eq 'finalizationRetry' -and
                $_.match.marker.kind -eq 'one' -and $_.match.marker.value -eq $rule.Marker -and
                ($rule.Selection -eq '*' -or ($_.match.selectionState.kind -eq 'one' -and
                        $_.match.selectionState.value -eq $rule.Selection)) })
        if ($matching.Count -eq 0 -or @($matching | Where-Object firstApplicableRetryWrite -ne $rule.Write).Count -ne 0) {
            Fail-Contract $artifact "retry cutoff drift for $($rule.Marker)/$($rule.Selection)"
        }
    }

    $before = @($rows | Where-Object { $_.match.trigger.value -eq 'cancelImmediatelyBeforeFirstApplicableRetryWrite' })
    $after = @($rows | Where-Object { $_.match.trigger.value -in @(
                'cancelAfterExactCleanupBegins', 'cancelAfterBranchPublicationBegins', 'cancelAfterTerminalRecordingBegins') })
    if ($before.Count -ne 4 -or $after.Count -ne 4) { Fail-Contract $artifact 'retry cancellation boundary pairs are incomplete' }
    if (@($rows | Where-Object { $_.match.invocation.value -eq 'finalizationRetry' -and $_.workingFiles -ne 'unchanged' }).Count -ne 0) {
        Fail-Contract $artifact 'finalization retry mutates working files'
    }

    return [pscustomobject]@{ Rows = $rows; RowIds = @($rows.id); Cells = $cells; Grammar = $grammar }
}

function Get-ProjectionPlan([string] $canonicalText, [string] $canonicalArtifact) {
    $block = Get-SingleMarkedBlock $canonicalText $script:ProjectionPlanStart $script:ProjectionPlanEnd $canonicalArtifact
    $plan = ConvertFrom-FencedJson $block.Content $canonicalArtifact 'grace.wdu.lifecycle-projection-plan/v1'
    Assert-ExactProperties $plan @('assignments', 'canonicalApplicabilityKeyCount', 'canonicalRowCount', 'schema') $canonicalArtifact
    $duplicates = @($plan.assignments | Group-Object artifact | Where-Object Count -ne 1)
    if ($duplicates.Count -ne 0) { Fail-Contract $canonicalArtifact "duplicate projection assignment '$($duplicates[0].Name)'" }
    return $plan
}

function Get-Projection([string] $text, [string] $artifact) {
    $block = Get-SingleMarkedBlock $text $script:ProjectionStart $script:ProjectionEnd $artifact
    $projection = ConvertFrom-FencedJson $block.Content $artifact 'grace.wdu.lifecycle-projection/v1'
    Assert-ExactProperties $projection @('artifact', 'canonical', 'proof', 'rowIds', 'schema') $artifact
    return [pscustomobject]@{ Block = $block; Value = $projection }
}

function Get-ProjectionJson($assignment) {
    $projection = [ordered]@{
        schema = 'grace.wdu.lifecycle-projection/v1'
        artifact = $assignment.artifact
        canonical = 'docs/Working Directory Update.md#normative-branch-lifecycle-table'
        rowIds = @($assignment.rowIds)
        proof = $assignment.proof
    }
    $json = $projection | ConvertTo-Json -Depth 10
    return @($script:ProjectionStart, '```json', $json, '```', $script:ProjectionEnd) -join "`n"
}

function Test-Projection($projection, $assignment, [string[]] $canonicalRowIds, [string] $path) {
    if ($projection.artifact -ne $assignment.artifact) { Fail-Contract $path "artifact '$($projection.artifact)' does not match '$($assignment.artifact)'" }
    if ($projection.canonical -ne 'docs/Working Directory Update.md#normative-branch-lifecycle-table') {
        Fail-Contract $path 'canonical lifecycle link drift'
    }
    $unknown = @($projection.rowIds | Where-Object { $_ -notin $canonicalRowIds })
    if ($unknown.Count -ne 0) { Fail-Contract $path "unknown row '$($unknown[0])'" }
    if (@($projection.rowIds | Group-Object | Where-Object Count -ne 1).Count -ne 0) { Fail-Contract $path 'duplicate row reference' }
    if (Compare-Object @($assignment.rowIds) @($projection.rowIds)) { Fail-Contract $path 'required row subset drift' }
    if ($projection.proof -ne $assignment.proof) { Fail-Contract $path 'proof responsibility drift' }
}

function Test-NoCompetingProjection([string] $text, $block, [string] $path) {
    $outside = $text.Remove($block.Start, $block.EndAfter - $block.Start)
    if ($outside -match 'grace\.wdu\.branch-lifecycle/v1') { Fail-Contract $path 'contains a second normative lifecycle table' }
    if ($outside -match '(?is)(publish|publication).{0,80}(before|then).{0,80}(clean|cleanup)' -or
        $outside -match '(?is)(cleanup\s+or\s+publication)(?!.*terminal)') {
        Fail-Contract $path 'contains competing or reversed lifecycle ordering prose'
    }
}

function Write-RenderedProjection([string] $sourcePath, [string] $text, $projectionBlock, [string] $replacement, [string] $outputRoot, [bool] $multiple) {
    $rendered = $text.Substring(0, $projectionBlock.Start) + $replacement + $text.Substring($projectionBlock.EndAfter)
    if ($multiple -or (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        if (-not (Test-Path -LiteralPath $outputRoot)) { [void](New-Item -ItemType Directory -Path $outputRoot) }
        $outputPath = Join-Path $outputRoot ([IO.Path]::GetFileName($sourcePath))
    } else {
        $parent = Split-Path -Parent $outputRoot
        if ($parent -and -not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent) }
        $outputPath = $outputRoot
    }
    if ([IO.Path]::GetFullPath($outputPath) -eq [IO.Path]::GetFullPath($sourcePath)) {
        Fail-Contract $sourcePath 'render output must not overwrite an input artifact'
    }
    [IO.File]::WriteAllText($outputPath, $rendered, [Text.UTF8Encoding]::new($false))
    return $outputPath
}

$canonical = Read-Utf8Text $CanonicalPath
$contract = ConvertFrom-FencedJson $canonical.Text $canonical.Path 'grace.wdu.branch-lifecycle/v1'
$validated = Test-LifecycleContract $contract $canonical.Path
$plan = Get-ProjectionPlan $canonical.Text $canonical.Path
if ($plan.canonicalRowCount -ne $validated.Rows.Count -or $plan.canonicalApplicabilityKeyCount -ne $validated.Cells.Count) {
    Fail-Contract $canonical.Path 'projection plan count does not match lifecycle contract'
}
$requiredArtifacts = @('adr-0011', 'epic-835', 'issue-842', 'issue-843', 'issue-846', 'issue-869', 'issue-870',
    'issue-871', 'issue-872')
if (Compare-Object $requiredArtifacts @($plan.assignments.artifact)) {
    Fail-Contract $canonical.Path 'projection plan does not contain the exact required artifact set'
}
foreach ($assignment in $plan.assignments) {
    Assert-ExactProperties $assignment @('artifact', 'proof', 'rowIds') "$($assignment.artifact) assignment"
    if (-not $assignment.proof) { Fail-Contract $assignment.artifact 'projection proof responsibility is empty' }
    if (@($assignment.rowIds).Count -eq 0) { Fail-Contract $assignment.artifact 'projection row assignment is empty' }
    $unknown = @($assignment.rowIds | Where-Object { $_ -notin $validated.RowIds })
    if ($unknown.Count -ne 0) { Fail-Contract $assignment.artifact "plan references unknown row '$($unknown[0])'" }
    if (@($assignment.rowIds | Group-Object | Where-Object Count -ne 1).Count -ne 0) {
        Fail-Contract $assignment.artifact 'plan contains duplicate row assignment'
    }
}
$assignedRowSet = @($plan.assignments.rowIds | Sort-Object -Unique)
if (Compare-Object @($validated.RowIds | Sort-Object) $assignedRowSet) {
    Fail-Contract $canonical.Path 'projection plan does not cover the complete canonical row set'
}

$decisionIds = @([regex]::Matches($canonical.Text, '(?m)^\| (DEC-\d{3}) \|') | ForEach-Object { $_.Groups[1].Value })
$requirementIds = @([regex]::Matches($canonical.Text, '(?m)^\| (REQ-\d{3}) \|') | ForEach-Object { $_.Groups[1].Value })
if ($decisionIds.Count -ne 9 -or @($decisionIds | Group-Object | Where-Object Count -ne 1).Count -ne 0) {
    Fail-Contract $canonical.Path 'expected nine unique decisions'
}
if ($requirementIds.Count -ne 17 -or @($requirementIds | Group-Object | Where-Object Count -ne 1).Count -ne 0) {
    Fail-Contract $canonical.Path 'expected 17 unique requirements'
}

$inputs = @($ProjectionPath) + @($OfflineIssueBodyPath)
$seenArtifacts = [Collections.Generic.HashSet[string]]::new()
$renderJobs = @()
foreach ($input in $inputs) {
    $artifact = Read-Utf8Text $input
    $projection = Get-Projection $artifact.Text $artifact.Path
    $assignment = @($plan.assignments | Where-Object artifact -eq $projection.Value.artifact)
    if ($assignment.Count -ne 1) { Fail-Contract $artifact.Path "artifact '$($projection.Value.artifact)' has no canonical assignment" }
    if (-not $seenArtifacts.Add($projection.Value.artifact)) { Fail-Contract $artifact.Path "artifact '$($projection.Value.artifact)' was supplied twice" }
    if ($PSCmdlet.ParameterSetName -eq 'Check') {
        Test-Projection $projection.Value $assignment[0] $validated.RowIds $artifact.Path
    }
    Test-NoCompetingProjection $artifact.Text $projection.Block $artifact.Path
    $renderJobs += [pscustomobject]@{
        Source = $artifact.Path
        Text = $artifact.Text
        Block = $projection.Block
        Replacement = Get-ProjectionJson $assignment[0]
    }
}

if ($inputs.Count -gt 0) {
    $requiredInputArtifacts = @()
    if ($ProjectionPath.Count -gt 0) {
        $requiredInputArtifacts += @($plan.assignments | Where-Object artifact -eq 'adr-0011' | Select-Object -ExpandProperty artifact)
    }
    if ($OfflineIssueBodyPath.Count -gt 0) {
        $requiredInputArtifacts += @($plan.assignments | Where-Object artifact -ne 'adr-0011' | Select-Object -ExpandProperty artifact)
    }
    $missingArtifacts = @($requiredInputArtifacts | Where-Object { $_ -notin $seenArtifacts })
    if ($missingArtifacts.Count -ne 0) { Fail-Contract 'projection packet' "missing required artifact '$($missingArtifacts[0])'" }
}

if ($PSCmdlet.ParameterSetName -eq 'Render') {
    $multiple = $renderJobs.Count -gt 1
    foreach ($job in $renderJobs) {
        $output = Write-RenderedProjection $job.Source $job.Text $job.Block $job.Replacement $RenderOutputPath $multiple
        Write-Output "rendered: $output"
    }
}

Write-Output "WDU lifecycle contract valid: rows=$($validated.Rows.Count) keys=$($validated.Cells.Count) projections=$($inputs.Count) decisions=$($decisionIds.Count) requirements=$($requirementIds.Count)"
