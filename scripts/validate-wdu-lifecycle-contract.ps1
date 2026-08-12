# Copyright (c) Scott Arbeit.
# Licensed under the MIT License.

<#
.SYNOPSIS
Validates the canonical Working Directory Update lifecycle and its marker-delimited projections.

.DESCRIPTION
 Runs offline and read-only by default. Pass RenderOutputPath to write corrected projection markers to a new, separate
 output directory; input artifacts are never rewritten. Rendering completes a private staging packet before a single
 directory publication. The destination must not exist, so a staging or publication failure leaves every input and any
 prior output packet intact rather than attempting a portable in-place replacement transaction.

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
$script:HistoricalEvidenceStart = '<!-- grace:wdu-lifecycle-historical-evidence:start -->'
$script:HistoricalEvidenceEnd = '<!-- grace:wdu-lifecycle-historical-evidence:end -->'

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

function Get-FencedJsonPayload([string] $text, [string] $artifact, [string] $schema) {
    $matches = [regex]::Matches($text, '(?s)```json\s*(\{.*?\})\s*```')
    $matching = @()
    foreach ($match in $matches) {
        try {
            Assert-NoDuplicateJsonProperties $match.Groups[1].Value $artifact
            $value = $match.Groups[1].Value | ConvertFrom-Json -Depth 100
        } catch { continue }
        if ($null -ne $value.PSObject.Properties['schema'] -and $value.schema -eq $schema) {
            $matching += [pscustomobject]@{ Json = $match.Groups[1].Value; Value = $value }
        }
    }
    if ($matching.Count -ne 1) { Fail-Contract $artifact "expected exactly one '$schema' JSON block" }
    return $matching[0]
}

function Assert-NoDuplicateJsonProperties([string] $json, [string] $artifact) {
    try { $document = [System.Text.Json.JsonDocument]::Parse($json) } catch { Fail-Contract $artifact 'invalid JSON payload' }
    try {
        $visit = $null
        $visit = {
            param($element, [string] $location)
            if ($element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $element.EnumerateObject()) {
                    if (-not $names.Add($property.Name)) { Fail-Contract $artifact "duplicate-equivalent JSON property '$location/$($property.Name)'" }
                    & $visit $property.Value "$location/$($property.Name)"
                }
            } elseif ($element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
                $index = 0; foreach ($item in $element.EnumerateArray()) { & $visit $item "$location[$index]"; $index++ }
            }
        }
        & $visit $document.RootElement '$'
    } finally { $document.Dispose() }
}

function ConvertFrom-FencedJson([string] $text, [string] $artifact, [string] $schema) {
    return (Get-FencedJsonPayload $text $artifact $schema).Value
}

function Get-NormalizedContentDigest([string] $jsonPayload) {
    $normalized = $jsonPayload -replace "`r`n|`r", "`n"
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
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

function Assert-AllowedProperties($value, [string[]] $names, [string] $artifact) {
    $unknown = @(Get-PropertyNames $value | Where-Object { $_ -notin $names })
    if ($unknown.Count -ne 0) { Fail-Contract $artifact "unknown JSON property '$($unknown[0])'" }
}

function Assert-String($value, [string] $property, [string] $artifact) {
    if ($null -eq $value.PSObject.Properties[$property] -or $value.$property -isnot [string]) {
        Fail-Contract $artifact "'$property' must be a string"
    }
}

function Assert-Boolean($value, [string] $property, [string] $artifact) {
    if ($null -eq $value.PSObject.Properties[$property] -or $value.$property -isnot [bool]) {
        Fail-Contract $artifact "'$property' must be a Boolean"
    }
}

function Assert-OptionalBoolean($value, [string] $property, [string] $artifact) {
    if ($null -ne $value.$property -and $value.$property -isnot [bool]) { Fail-Contract $artifact "'$property' must be a Boolean or null" }
}

function Assert-OptionalString($value, [string] $property, [string] $artifact) {
    if ($null -ne $value.$property -and $value.$property -isnot [string]) { Fail-Contract $artifact "'$property' must be a string or null" }
}

function Assert-Array($value, [string] $property, [string] $artifact) {
    if ($null -eq $value.PSObject.Properties[$property] -or $value.$property -is [string] -or
        $value.$property -isnot [System.Collections.IEnumerable]) {
        Fail-Contract $artifact "'$property' must be an array"
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
    Assert-ExactProperties $contract @('boundaries', 'doctorCommand', 'machineGrammar', 'order',
        'retryAdmission', 'rows', 'schema') $artifact
    if ($contract.schema -ne 'grace.wdu.branch-lifecycle/v1') { Fail-Contract $artifact 'unexpected lifecycle schema' }
    Assert-String $contract 'schema' $artifact
    Assert-Array $contract 'rows' $artifact
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
    $durableActions = @(
        'applyFreshPlan', 'attemptCleanExactMarker', 'attemptCleanOnlyExactOwnedMarker', 'attemptPublishSelectedBranch',
        'attemptTerminalRecording', 'beginTrackedWorkingTreeMutation', 'buildFreshPlanFromCurrentTrackedGraph',
        'checkCancellationImmediatelyBeforeMutation', 'cleanExactMarker', 'cleanOnlyExactOwnedMarker',
        'continueByActualEvidence', 'createExactOwnedMarkerWithFreshAttemptToken', 'discardEveryPriorPlan',
        'ignoreCancellation', 'inspectPostCompletionMarker', 'neverRepublishWithoutProof', 'proveCurrentBranchUnchanged',
        'provePublication', 'proveSelectedBranch', 'publishSelectedBranch', 'recordSqliteLocalCompletion', 'recordTerminal',
        'rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan', 'replaceAttemptTokenWithFreshAttemptToken',
        'rereadAcceptedRevisionAndCompleteStatusFingerprint', 'rereadCompleteLocalStatusAndMarker',
        'rereadMarkerSchemaScopeOperationTargetAndAttemptToken', 'retainEvidence', 'retainExactMarker',
        'retainExactMarkerEvidence', 'retainMarker', 'retainMarkerEvidence', 'retainPending', 'verifyCompleteTargetRoot',
        'verifyExactOperationIdentity', 'verifyExactTargetIdentity', 'verifyFreshPlanAgainstReread', 'verifyMarkerSchema',
        'verifyRepositoryAndLocalRootScope'
    )
    $outcomeDurableResult = @{
        FinalizationIncomplete = 'pending'
        Rejected = 'noCompletion'
        Unchanged = 'existingTerminal'
        Updated = 'terminal'
        UpdateIncomplete = 'noCompletion'
    }
    foreach ($row in $rows) {
        Assert-AllowedProperties $row @('branchIdentity', 'doctorGuidance', 'durableResult', 'exitClass',
            'firstApplicableRetryWrite', 'id', 'match', 'outcome', 'requiredActions', 'resultingMarker',
            'workingFiles', 'nextRows') $row.id
        Assert-String $row 'id' $artifact
        Assert-String $row 'firstApplicableRetryWrite' $row.id
        Assert-Array $row 'requiredActions' $row.id
        Assert-OptionalString $row 'workingFiles' $row.id
        Assert-OptionalString $row 'branchIdentity' $row.id
        Assert-OptionalString $row 'durableResult' $row.id
        Assert-OptionalString $row 'exitClass' $row.id
        Assert-OptionalBoolean $row 'doctorGuidance' $row.id
        if ($null -ne $row.PSObject.Properties['resultingMarker']) { Assert-OptionalString $row 'resultingMarker' $row.id }
        if ($null -ne $row.PSObject.Properties['outcome']) { Assert-OptionalString $row 'outcome' $row.id }
        if ($null -ne $row.PSObject.Properties['nextRows']) { Assert-Array $row 'nextRows' $row.id }
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
        $unknownActions = @($row.requiredActions | Where-Object { $_ -notin $durableActions })
        if ($unknownActions.Count -ne 0) { Fail-Contract $row.id "unknown durable action '$($unknownActions[0])'" }
        if ($null -eq $row.outcome) {
            if ($null -eq $row.PSObject.Properties['nextRows'] -or @($row.nextRows).Count -eq 0) {
                Fail-Contract $row.id 'routing row has no nextRows'
            }
        } elseif ($row.outcome -eq 'FinalizationIncomplete' -and
            ($row.exitClass -ne 'nonzero' -or $row.doctorGuidance -ne $true)) {
            Fail-Contract $row.id 'FinalizationIncomplete lacks nonzero exit or Doctor guidance'
        } elseif ($null -eq $outcomeDurableResult[$row.outcome]) {
            Fail-Contract $row.id "unknown outcome '$($row.outcome)'"
        } elseif ($row.durableResult -ne $outcomeDurableResult[$row.outcome]) {
            Fail-Contract $row.id "outcome '$($row.outcome)' requires durableResult '$($outcomeDurableResult[$row.outcome])'"
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

    # The canonical table has a closed mutation relation. These are structural rules over each normalized row rather
    # than a duplicate action table: publication may only establish a previously selected Reference, and recording is
    # always preceded by the proof appropriate to the selected durable identity.
    foreach ($row in $rows) {
        $actions = @($row.requiredActions)
        $invocations = @(Expand-Predicate $grammar invocation $row.match.invocation $row.id)
        $markers = @(Expand-Predicate $grammar marker $row.match.marker $row.id)
        $selections = @(Expand-Predicate $grammar selectionState $row.match.selectionState $row.id)
        $publicationActions = @($actions | Where-Object { $_ -in @('publishSelectedBranch', 'attemptPublishSelectedBranch') })
        if ($publicationActions.Count -gt 0 -and ($selections -notcontains 'referencePrevious' -or
                @($markers | Where-Object { $_ -in @('differentOperation', 'malformed', 'unsupported', 'unreadable', 'exactCleanupFailed') }).Count -gt 0)) {
            Fail-Contract $row.id 'Branch publication is only legal for missing-marker Reference previous-Branch finalization'
        }
        if ($publicationActions.Count -gt 1) { Fail-Contract $row.id 'row contains more than one Branch publication action' }
        $terminalActions = @($actions | Where-Object { $_ -in @('recordTerminal', 'attemptTerminalRecording') })
        if ($terminalActions.Count -gt 1) { Fail-Contract $row.id 'row contains more than one terminal recording action' }
        if ($terminalActions.Count -eq 1) {
            $requiredProof = if ($selections -contains 'referencePrevious') { 'provePublication' } elseif (
                $selections -contains 'referenceSelected') { 'proveSelectedBranch' } elseif (
                $selections -contains 'directoryVersion') { 'proveCurrentBranchUnchanged' } else { $null }
            if ($null -eq $requiredProof -or [array]::IndexOf($actions, $requiredProof) -lt 0 -or
                [array]::IndexOf($actions, $requiredProof) -gt [array]::IndexOf($actions, $terminalActions[0])) {
                Fail-Contract $row.id 'terminal recording requires prior durable identity proof'
            }
        }
        if ($row.firstApplicableRetryWrite -eq 'terminalRecording' -and $selections -notcontains 'referenceSelected' -and
            $selections -notcontains 'directoryVersion') {
            Fail-Contract $row.id 'terminal recording cutoff requires selected Reference or DirectoryVersion'
        }
    }

    $terminalReplays = @($rows | Where-Object { $_.match.invocation.kind -eq 'one' -and $_.match.invocation.value -eq 'terminalReplay' })
    if ($terminalReplays.Count -ne 1) { Fail-Contract $artifact 'expected one terminal replay row' }
    foreach ($row in $terminalReplays) {
        if ($row.outcome -ne 'Unchanged' -or $row.durableResult -ne 'existingTerminal' -or
            $row.workingFiles -ne 'unchanged' -or $row.branchIdentity -ne 'unchanged' -or
            @($row.requiredActions).Count -ne 0 -or $null -ne $row.PSObject.Properties['nextRows']) {
            Fail-Contract $row.id 'terminal replay must be unchanged and mutation-free'
        }
    }

    $refusalActions = @('publishSelectedBranch', 'attemptPublishSelectedBranch', 'recordTerminal', 'attemptTerminalRecording')
    foreach ($row in @($rows | Where-Object { $_.outcome -eq 'Rejected' -or $_.requiredActions -contains 'retainEvidence' -or $_.requiredActions -contains 'retainMarkerEvidence' })) {
        $forbidden = @($row.requiredActions | Where-Object { $_ -in $refusalActions })
        if ($forbidden.Count -ne 0) { Fail-Contract $row.id "refusal or evidence-preservation row cannot '$($forbidden[0])'" }
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

function Get-ProjectionPlan([string] $canonicalText, [string] $canonicalArtifact, [string] $canonicalContentDigest) {
    $block = Get-SingleMarkedBlock $canonicalText $script:ProjectionPlanStart $script:ProjectionPlanEnd $canonicalArtifact
    $plan = ConvertFrom-FencedJson $block.Content $canonicalArtifact 'grace.wdu.lifecycle-projection-plan/v1'
    Assert-ExactProperties $plan @('assignments', 'canonicalApplicabilityKeyCount', 'canonicalContentDigest', 'canonicalRowCount', 'schema') $canonicalArtifact
    Assert-String $plan 'schema' $canonicalArtifact
    Assert-Array $plan 'assignments' $canonicalArtifact
    if ($plan.canonicalContentDigest -notmatch '^[a-f0-9]{64}$') { Fail-Contract $canonicalArtifact 'projection plan digest is missing or malformed' }
    if ($plan.canonicalContentDigest -ne $canonicalContentDigest) { Fail-Contract $canonicalArtifact 'projection plan digest does not match canonical lifecycle content' }
    $duplicates = @($plan.assignments | Group-Object artifact | Where-Object Count -ne 1)
    if ($duplicates.Count -ne 0) { Fail-Contract $canonicalArtifact "duplicate projection assignment '$($duplicates[0].Name)'" }
    return $plan
}

function Get-Projection([string] $text, [string] $artifact, [bool] $strict) {
    $block = Get-SingleMarkedBlock $text $script:ProjectionStart $script:ProjectionEnd $artifact
    $projection = ConvertFrom-FencedJson $block.Content $artifact 'grace.wdu.lifecycle-projection/v1'
    if ($strict) {
        Assert-ExactProperties $projection @('artifact', 'canonical', 'canonicalContentDigest', 'proof', 'rowIds', 'schema') $artifact
    } else {
        $renderProperties = Get-PropertyNames $projection
        $allowed = @('artifact', 'canonical', 'canonicalContentDigest', 'proof', 'rowIds', 'schema')
        $unknown = @($renderProperties | Where-Object { $_ -notin $allowed })
        if ($unknown.Count -ne 0 -or @($renderProperties | Where-Object { $_ -notin @('artifact', 'canonical', 'proof', 'rowIds', 'schema') }).Count -gt 1) {
            Fail-Contract $artifact 'renderable projection has malformed properties'
        }
        foreach ($required in @('artifact', 'canonical', 'proof', 'rowIds', 'schema')) {
            if ($null -eq $projection.PSObject.Properties[$required]) { Fail-Contract $artifact "renderable projection is missing '$required'" }
        }
    }
    foreach ($name in @('artifact', 'canonical', 'proof', 'schema')) { Assert-String $projection $name $artifact }
    Assert-Array $projection 'rowIds' $artifact
    if ($strict) { Assert-String $projection 'canonicalContentDigest' $artifact }
    return [pscustomobject]@{ Block = $block; Value = $projection }
}

function Get-ProjectionJson($assignment, [string] $canonicalContentDigest) {
    $projection = [ordered]@{
        schema = 'grace.wdu.lifecycle-projection/v1'
        artifact = $assignment.artifact
        canonical = 'docs/Working Directory Update.md#normative-branch-lifecycle-table'
        canonicalContentDigest = $canonicalContentDigest
        rowIds = @($assignment.rowIds)
        proof = $assignment.proof
    }
    $json = $projection | ConvertTo-Json -Depth 10
    return @($script:ProjectionStart, '```json', $json, '```', $script:ProjectionEnd) -join "`n"
}

function Test-Projection($projection, $assignment, [string[]] $canonicalRowIds, [string] $canonicalContentDigest, [string] $path) {
    if ($projection.artifact -ne $assignment.artifact) { Fail-Contract $path "artifact '$($projection.artifact)' does not match '$($assignment.artifact)'" }
    if ($projection.canonical -ne 'docs/Working Directory Update.md#normative-branch-lifecycle-table') {
        Fail-Contract $path 'canonical lifecycle link drift'
    }
    if ($projection.canonicalContentDigest -notmatch '^[a-f0-9]{64}$') { Fail-Contract $path 'projection digest is missing or malformed' }
    if ($projection.canonicalContentDigest -ne $canonicalContentDigest) { Fail-Contract $path 'projection digest does not match canonical lifecycle content' }
    $unknown = @($projection.rowIds | Where-Object { $_ -notin $canonicalRowIds })
    if ($unknown.Count -ne 0) { Fail-Contract $path "unknown row '$($unknown[0])'" }
    if (@($projection.rowIds | Group-Object | Where-Object Count -ne 1).Count -ne 0) { Fail-Contract $path 'duplicate row reference' }
    if (Compare-Object @($assignment.rowIds) @($projection.rowIds)) { Fail-Contract $path 'required row subset drift' }
    if ($projection.proof -ne $assignment.proof) { Fail-Contract $path 'proof responsibility drift' }
}

function Test-NoCompetingProjection([string] $text, $block, [string] $path) {
    $outside = $text.Remove($block.Start, $block.EndAfter - $block.Start)
    if ($outside -match 'grace\.wdu\.branch-lifecycle/v1') { Fail-Contract $path 'contains a second normative lifecycle table' }
    $historyStarts = [regex]::Matches($outside, [regex]::Escape($script:HistoricalEvidenceStart))
    $historyEnds = [regex]::Matches($outside, [regex]::Escape($script:HistoricalEvidenceEnd))
    if ($historyStarts.Count -ne $historyEnds.Count -or $historyStarts.Count -gt 1) {
        Fail-Contract $path 'historical evidence markers are unmatched or repeated'
    }
    if ($historyStarts.Count -eq 1) {
        $historyStart = $historyStarts[0].Index + $historyStarts[0].Length
        $historyEnd = $historyEnds[0].Index
        if ($historyEnd -le $historyStart) { Fail-Contract $path 'historical evidence markers are reversed' }
        $history = $outside.Substring($historyStart, $historyEnd - $historyStart).Trim()
        # History deliberately carries only a stable supersession reference, never operational prose.
        if ($history -notmatch '^Historical supersession reference: \[[^\]]+\]\([^\)]+\)\.$') {
            Fail-Contract $path 'historical evidence is not a stable supersession reference'
        }
        $outside = $outside.Remove($historyEnds[0].Index, $historyEnds[0].Length).Remove($historyStarts[0].Index, $historyStarts[0].Length)
        $outside = $outside.Remove($historyStarts[0].Index, $historyEnd - $historyStart)
    }
    $paragraphs = @($outside -split '(?:\r?\n){2,}')
    foreach ($paragraph in $paragraphs) {
        $reversed = $paragraph -match '(?is)\b(?:publish|publication)\b.{0,60}(?:\bbefore\b|\bthen\b|\bafter\b|-[ ]?before-)\s*.{0,60}\b(?:clean|cleanup)\b'
        $copiedSequence = $paragraph -match '(?is)\b(?:clean|cleanup)\b.{0,160}\b(?:publish|publication)\b.{0,160}\bprove\b.{0,160}\bterminal\b'
        $selectedReferenceSequence = $paragraph -match '(?is)\b(?:prove|proof)\b.{0,120}\bselected\b.{0,120}\b(?:record|terminal)\b'
        if ($reversed -or $copiedSequence -or $selectedReferenceSequence) {
            Fail-Contract $path 'contains competing lifecycle source outside its projection'
        }
    }
}

function Get-FilesystemIdentity([string] $path, [string] $artifact) {
    $full = [IO.Path]::GetFullPath($path)
    $parts = [Collections.Generic.Stack[string]]::new()
    $cursor = $full
    while (-not (Test-Path -LiteralPath $cursor)) {
        $leaf = Split-Path -Leaf $cursor
        if (-not $leaf) { Fail-Contract $artifact "cannot establish filesystem identity for '$full'" }
        $parts.Push($leaf); $cursor = Split-Path -Parent $cursor
    }
    $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        try { $item = $item.ResolveLinkTarget($true) } catch { Fail-Contract $artifact "cannot prove reparse-point safety for '$cursor'" }
        if ($null -eq $item) { Fail-Contract $artifact "cannot prove reparse-point safety for '$cursor'" }
    }
    $resolved = $item.FullName
    while ($parts.Count -gt 0) { $resolved = Join-Path $resolved $parts.Pop() }
    return [IO.Path]::GetFullPath($resolved)
}

function Test-RenderPreflight($jobs) {
    $inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($job in $jobs) { [void]$inputs.Add((Get-FilesystemIdentity $job.Source $job.Source)) }
    foreach ($job in $jobs) {
        $identity = Get-FilesystemIdentity $job.Output $job.Source
        if (-not $destinations.Add($identity)) { Fail-Contract $job.Source "duplicate render destination '$identity'" }
        if ($inputs.Contains($identity)) { Fail-Contract $job.Source 'render output must not overwrite an input artifact' }
    }
}

function Write-RenderedProjection($job) {
    $rendered = $job.Text.Substring(0, $job.Block.Start) + $job.Replacement + $job.Text.Substring($job.Block.EndAfter)
    $parent = Split-Path -Parent $job.Output
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent) }
    [IO.File]::WriteAllText($job.Output, $rendered, [Text.UTF8Encoding]::new($false))
    return $job.Output
}

$canonical = Read-Utf8Text $CanonicalPath
$lifecyclePayload = Get-FencedJsonPayload $canonical.Text $canonical.Path 'grace.wdu.branch-lifecycle/v1'
$contract = $lifecyclePayload.Value
$canonicalContentDigest = Get-NormalizedContentDigest $lifecyclePayload.Json
$validated = Test-LifecycleContract $contract $canonical.Path
$plan = Get-ProjectionPlan $canonical.Text $canonical.Path $canonicalContentDigest
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
        Assert-String $assignment 'artifact' $canonical.Path
        Assert-String $assignment 'proof' $canonical.Path
        Assert-Array $assignment 'rowIds' $canonical.Path
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
    $projection = Get-Projection $artifact.Text $artifact.Path ($PSCmdlet.ParameterSetName -eq 'Check')
    $assignment = @($plan.assignments | Where-Object artifact -eq $projection.Value.artifact)
    if ($assignment.Count -ne 1) { Fail-Contract $artifact.Path "artifact '$($projection.Value.artifact)' has no canonical assignment" }
    if (-not $seenArtifacts.Add($projection.Value.artifact)) { Fail-Contract $artifact.Path "artifact '$($projection.Value.artifact)' was supplied twice" }
    if ($PSCmdlet.ParameterSetName -eq 'Check') {
        Test-Projection $projection.Value $assignment[0] $validated.RowIds $canonicalContentDigest $artifact.Path
    }
    Test-NoCompetingProjection $artifact.Text $projection.Block $artifact.Path
    $renderJobs += [pscustomobject]@{
        Source = $artifact.Path
        Text = $artifact.Text
        Block = $projection.Block
        Replacement = Get-ProjectionJson $assignment[0] $canonicalContentDigest
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
    $outputRoot = [IO.Path]::GetFullPath($RenderOutputPath)
    if (Test-Path -LiteralPath $outputRoot) { Fail-Contract $outputRoot 'render output directory must not already exist; prior packet is preserved' }
    $parent = Split-Path -Parent $outputRoot
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { Fail-Contract $outputRoot 'render output parent directory must already exist' }
    $staging = Join-Path $parent ".grace-wdu-staging-$([Guid]::NewGuid().ToString('N'))"
    foreach ($job in $renderJobs) { $job | Add-Member -NotePropertyName Output -NotePropertyValue (Join-Path $staging ([IO.Path]::GetFileName($job.Source))) }
    Test-RenderPreflight $renderJobs
    try {
        foreach ($job in $renderJobs) { $null = Write-RenderedProjection $job }
        if ($env:GRACE_WDU_RENDER_FAIL_AFTER_STAGING_WRITE -eq '1') { throw 'injected staging publication failure' }
        Move-Item -LiteralPath $staging -Destination $outputRoot -ErrorAction Stop
        Get-ChildItem -LiteralPath $outputRoot -File | Sort-Object Name | ForEach-Object { Write-Output "rendered: $($_.FullName)" }
    } finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
}

Write-Output "WDU lifecycle contract valid: rows=$($validated.Rows.Count) keys=$($validated.Cells.Count) projections=$($inputs.Count) decisions=$($decisionIds.Count) requirements=$($requirementIds.Count)"
