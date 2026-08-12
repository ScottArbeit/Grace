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
    $matches = [regex]::Matches($text, '(?s)```json\s*(.*?)\s*```')
    $matching = @()
    foreach ($match in $matches) {
        $json = $match.Groups[1].Value
        try {
            $document = [System.Text.Json.JsonDocument]::Parse($json)
            $raw = $document.RootElement.Clone()
            $document.Dispose()
            $value = $json | ConvertFrom-Json -Depth 100
        } catch {
            $declaresTarget = $false
            foreach ($propertyMatch in [regex]::Matches($json, '(?s)"((?:\\.|[^"\\])*)"\s*:\s*"((?:\\.|[^"\\])*)"')) {
                try {
                    $probe = "{`"name`":`"$($propertyMatch.Groups[1].Value)`",`"value`":`"$($propertyMatch.Groups[2].Value)`"}" | ConvertFrom-Json
                    if ($probe.name -ieq 'schema' -and $probe.value -ceq $schema) { $declaresTarget = $true; break }
                } catch { }
            }
            if ($declaresTarget) { Fail-Contract $artifact "malformed JSON candidate for '$schema'" }
            continue
        }
        $schemaProperties = @($raw.EnumerateObject() | Where-Object { $_.Name -ieq 'schema' })
        if ($schemaProperties.Count -eq 0) { continue }
        # Schema candidacy comes from decoded top-level property names, never from raw text embedded in another value.
        Assert-NoDuplicateJsonProperties $json $artifact
        $targetProperties = @($schemaProperties | Where-Object {
                $_.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String -and $_.Value.GetString() -ceq $schema
            })
        if ($targetProperties.Count -gt 1) { Fail-Contract $artifact "duplicate target schema declaration '$schema'" }
        if ($targetProperties.Count -eq 1) {
            $matching += [pscustomobject]@{ Json = $json; Value = $value; Raw = $raw }
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

function Assert-JsonKind($element, [System.Text.Json.JsonValueKind] $kind, [string] $artifact, [string] $location) {
    if ($element.ValueKind -ne $kind) { Fail-Contract $artifact "$location must be a $kind JSON value" }
}

function Get-JsonProperty($element, [string] $name, [string] $artifact, [string] $location) {
    $property = @($element.EnumerateObject() | Where-Object { $_.Name -ceq $name })
    if ($property.Count -ne 1) { Fail-Contract $artifact "$location is missing JSON property '$name'" }
    return $property[0].Value
}

function Assert-JsonExactProperties($element, [string[]] $names, [string] $artifact, [string] $location) {
    Assert-JsonKind $element ([System.Text.Json.JsonValueKind]::Object) $artifact $location
    $actual = @($element.EnumerateObject() | ForEach-Object Name | Sort-Object)
    if (Compare-Object ($names | Sort-Object) $actual) {
        Fail-Contract $artifact "$location has malformed JSON properties: $($actual -join ', ')"
    }
}

function Assert-JsonString($element, [string] $artifact, [string] $location, [string] $fixedValue = '') {
    Assert-JsonKind $element ([System.Text.Json.JsonValueKind]::String) $artifact $location
    if ($fixedValue -and $element.GetString() -cne $fixedValue) { Fail-Contract $artifact "$location must equal '$fixedValue'" }
}

function Assert-JsonStringOrNull($element, [string] $artifact, [string] $location) {
    if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::String -and $element.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        Fail-Contract $artifact "$location must be a string or null JSON value"
    }
}

function Assert-JsonBooleanOrNull($element, [string] $artifact, [string] $location) {
    if ($element.ValueKind -notin @([System.Text.Json.JsonValueKind]::True, [System.Text.Json.JsonValueKind]::False,
            [System.Text.Json.JsonValueKind]::Null)) { Fail-Contract $artifact "$location must be a Boolean or null JSON value" }
}

function Get-JsonStringArray($element, [string] $artifact, [string] $location) {
    Assert-JsonKind $element ([System.Text.Json.JsonValueKind]::Array) $artifact $location
    $values = @()
    $index = 0
    foreach ($item in $element.EnumerateArray()) {
        Assert-JsonString $item $artifact "$location[$index]"
        $values += $item.GetString(); $index++
    }
    return $values
}

function Assert-JsonInteger($element, [string] $artifact, [string] $location) {
    Assert-JsonKind $element ([System.Text.Json.JsonValueKind]::Number) $artifact $location
    $number = 0
    if (-not $element.TryGetInt32([ref] $number)) { Fail-Contract $artifact "$location must be an integer JSON number" }
    return $number
}

function Assert-JsonPredicate($element, [string] $axis, [string] $artifact, [string] $location) {
    Assert-JsonKind $element ([System.Text.Json.JsonValueKind]::Object) $artifact $location
    $kind = Get-JsonProperty $element 'kind' $artifact $location
    Assert-JsonString $kind $artifact "$location/kind"
    switch ($kind.GetString()) {
        'one' { Assert-JsonExactProperties $element @('kind', 'value') $artifact $location; Assert-JsonString (Get-JsonProperty $element 'value' $artifact $location) $artifact "$location/value" }
        'set' { Assert-JsonExactProperties $element @('kind', 'values') $artifact $location; $null = Get-JsonStringArray (Get-JsonProperty $element 'values' $artifact $location) $artifact "$location/values" }
        'aggregate' { Assert-JsonExactProperties $element @('kind', 'name') $artifact $location; Assert-JsonString (Get-JsonProperty $element 'name' $artifact $location) $artifact "$location/name" }
        default { Fail-Contract $artifact "$location/kind has unknown predicate kind '$($kind.GetString())'" }
    }
}

function Test-LifecycleSchema($payload, [string] $artifact) {
    $root = $payload.Raw
    Assert-JsonExactProperties $root @('boundaries', 'doctorCommand', 'machineGrammar', 'order', 'retryAdmission', 'rows', 'schema') $artifact '$'
    Assert-JsonString (Get-JsonProperty $root 'schema' $artifact '$') $artifact '$/schema' 'grace.wdu.branch-lifecycle/v1'
    Assert-JsonString (Get-JsonProperty $root 'doctorCommand' $artifact '$') $artifact '$/doctorCommand' 'grace doctor --repair-local-state'

    $grammar = Get-JsonProperty $root 'machineGrammar' $artifact '$'
    Assert-JsonExactProperties $grammar @('aggregates', 'concreteEnums', 'encoding', 'expansion', 'overlap', 'predicateAxes', 'terminalReplay') $artifact '$/machineGrammar'
    $axes = @('invocation', 'trigger', 'marker', 'selectionState')
    Assert-ExactMembers (Get-JsonStringArray (Get-JsonProperty $grammar 'predicateAxes' $artifact '$/machineGrammar') $artifact '$/machineGrammar/predicateAxes') $axes $artifact 'machineGrammar predicateAxes'

    $encoding = Get-JsonProperty $grammar 'encoding' $artifact '$/machineGrammar'
    $encodingExpectations = [ordered]@{
        one = @{ Meaning = 'one concrete value'; Shape = @('kind', 'one', 'value', '<concrete-enum-member>') }
        set = @{ Meaning = 'nonempty duplicate-free union of concrete values'; Shape = @('kind', 'set', 'values', '<concrete-enum-member>') }
        aggregate = @{ Meaning = 'exact declared expansion; aggregates cannot nest'; Shape = @('kind', 'aggregate', 'name', '<declared-axis-aggregate>') }
    }
    Assert-JsonExactProperties $encoding @($encodingExpectations.Keys) $artifact '$/machineGrammar/encoding'
    foreach ($name in $encodingExpectations.Keys) {
        $entry = Get-JsonProperty $encoding $name $artifact '$/machineGrammar/encoding'
        Assert-JsonExactProperties $entry @('jsonShape', 'meaning') $artifact "$/machineGrammar/encoding/$name"
        Assert-JsonString (Get-JsonProperty $entry 'meaning' $artifact "$/machineGrammar/encoding/$name") $artifact "$/machineGrammar/encoding/$name/meaning" $encodingExpectations[$name].Meaning
        $shape = Get-JsonProperty $entry 'jsonShape' $artifact "$/machineGrammar/encoding/$name"
        Assert-JsonExactProperties $shape @($encodingExpectations[$name].Shape[0], $encodingExpectations[$name].Shape[2]) $artifact "$/machineGrammar/encoding/$name/jsonShape"
        Assert-JsonString (Get-JsonProperty $shape $encodingExpectations[$name].Shape[0] $artifact "$/machineGrammar/encoding/$name/jsonShape") $artifact "$/machineGrammar/encoding/$name/jsonShape/kind" $encodingExpectations[$name].Shape[1]
        $shapeValue = Get-JsonProperty $shape $encodingExpectations[$name].Shape[2] $artifact "$/machineGrammar/encoding/$name/jsonShape"
        if ($name -eq 'set') {
            $values = Get-JsonStringArray $shapeValue $artifact "$/machineGrammar/encoding/$name/jsonShape/values"
            Assert-ExactMembers $values @('<concrete-enum-member>') $artifact "encoding $name jsonShape values"
        } else {
            Assert-JsonString $shapeValue $artifact "$/machineGrammar/encoding/$name/jsonShape/$($encodingExpectations[$name].Shape[2])" $encodingExpectations[$name].Shape[3]
        }
    }

    $concreteEnums = Get-JsonProperty $grammar 'concreteEnums' $artifact '$/machineGrammar'
    Assert-JsonExactProperties $concreteEnums @('exitClass', 'firstApplicableRetryWrite', 'invocation', 'marker', 'selectionState', 'trigger') $artifact '$/machineGrammar/concreteEnums'
    foreach ($name in @('exitClass', 'firstApplicableRetryWrite', 'invocation', 'marker', 'selectionState', 'trigger')) {
        $null = Get-JsonStringArray (Get-JsonProperty $concreteEnums $name $artifact '$/machineGrammar/concreteEnums') $artifact "$/machineGrammar/concreteEnums/$name"
    }
    $aggregates = Get-JsonProperty $grammar 'aggregates' $artifact '$/machineGrammar'
    Assert-JsonExactProperties $aggregates @('marker', 'selectionState') $artifact '$/machineGrammar/aggregates'
    foreach ($axis in @('marker', 'selectionState')) {
        $aggregate = Get-JsonProperty $aggregates $axis $artifact '$/machineGrammar/aggregates'
        Assert-JsonKind $aggregate ([System.Text.Json.JsonValueKind]::Object) $artifact "$/machineGrammar/aggregates/$axis"
        foreach ($property in $aggregate.EnumerateObject()) { $null = Get-JsonStringArray $property.Value $artifact "$/machineGrammar/aggregates/$axis/$($property.Name)" }
    }
    $boundaries = Get-JsonProperty $root 'boundaries' $artifact '$'
    Assert-JsonExactProperties $boundaries @('firstApplicableRetryWrite', 'firstWorkingTreeMutation', 'sqliteLocalCompletion') $artifact '$/boundaries'
    Assert-JsonString (Get-JsonProperty $boundaries 'firstWorkingTreeMutation' $artifact '$/boundaries') $artifact '$/boundaries/firstWorkingTreeMutation' 'first tracked working-path mutation'
    Assert-JsonString (Get-JsonProperty $boundaries 'sqliteLocalCompletion' $artifact '$/boundaries') $artifact '$/boundaries/sqliteLocalCompletion' 'atomic verified status, object metadata, and pending-operation write'
    Assert-JsonString (Get-JsonProperty $boundaries 'firstApplicableRetryWrite' $artifact '$/boundaries') $artifact '$/boundaries/firstApplicableRetryWrite' 'exactCleanup, branchPublication, or terminalRecording selected from persisted facts'
    $expansion = Get-JsonProperty $grammar 'expansion' $artifact '$/machineGrammar'
    Assert-JsonExactProperties $expansion @('aggregateMembers', 'example', 'rule', 'setMembers') $artifact '$/machineGrammar/expansion'
    Assert-JsonString (Get-JsonProperty $expansion 'rule' $artifact '$/machineGrammar/expansion') $artifact '$/machineGrammar/expansion/rule' 'Resolve each match axis by its kind, then take the Cartesian product across all four axes.'
    Assert-JsonString (Get-JsonProperty $expansion 'setMembers' $artifact '$/machineGrammar/expansion') $artifact '$/machineGrammar/expansion/setMembers' 'Set values are concrete members of that axis only; unknown values, empty sets, duplicates, and mixed shapes are invalid.'
    Assert-JsonString (Get-JsonProperty $expansion 'aggregateMembers' $artifact '$/machineGrammar/expansion') $artifact '$/machineGrammar/expansion/aggregateMembers' 'Aggregate names are valid only on the axis where declared; unknown names and aggregate tokens inside sets are invalid.'
    Assert-JsonString (Get-JsonProperty $expansion 'example' $artifact '$/machineGrammar/expansion') $artifact '$/machineGrammar/expansion/example' 'WDU-LC-100 expands five marker values times four selection states into 20 applicable cells.'
    $overlap = Get-JsonProperty $grammar 'overlap' $artifact '$/machineGrammar'
    Assert-JsonExactProperties $overlap @('applicabilityKey', 'routing', 'rule') $artifact '$/machineGrammar/overlap'
    Assert-ExactMembers (Get-JsonStringArray (Get-JsonProperty $overlap 'applicabilityKey' $artifact '$/machineGrammar/overlap') $artifact '$/machineGrammar/overlap/applicabilityKey') $axes $artifact 'machineGrammar overlap applicability key'
    Assert-JsonString (Get-JsonProperty $overlap 'rule' $artifact '$/machineGrammar/overlap') $artifact '$/machineGrammar/overlap/rule' 'Expanded applicability keys must be disjoint; duplicate keys are invalid and there is no first-row-wins precedence.'
    Assert-JsonString (Get-JsonProperty $overlap 'routing' $artifact '$/machineGrammar/overlap') $artifact '$/machineGrammar/overlap/routing' 'A routing row selects only its declared nextRows after its own key matches; nextRows do not create precedence.'
    $terminal = Get-JsonProperty $grammar 'terminalReplay' $artifact '$/machineGrammar'
    Assert-JsonExactProperties $terminal @('effects', 'markerExpansion', 'row', 'selectionExpansion') $artifact '$/machineGrammar/terminalReplay'
    Assert-JsonString (Get-JsonProperty $terminal 'row' $artifact '$/machineGrammar/terminalReplay') $artifact '$/machineGrammar/terminalReplay/row' 'WDU-LC-003'
    Assert-JsonString (Get-JsonProperty $terminal 'selectionExpansion' $artifact '$/machineGrammar/terminalReplay') $artifact '$/machineGrammar/terminalReplay/selectionExpansion' 'persisted expands to all four concrete persisted selection states'
    Assert-JsonString (Get-JsonProperty $terminal 'markerExpansion' $artifact '$/machineGrammar/terminalReplay') $artifact '$/machineGrammar/terminalReplay/markerExpansion' 'any expands to all recognized marker values because exact terminal SQLite evidence is authoritative'
    Assert-JsonString (Get-JsonProperty $terminal 'effects' $artifact '$/machineGrammar/terminalReplay') $artifact '$/machineGrammar/terminalReplay/effects' 'No marker, working-file, Branch, completion, or retry write occurs; invocation cancellation is ignored and the outcome is Unchanged.'
    $retry = Get-JsonProperty $root 'retryAdmission' $artifact '$'
    Assert-JsonExactProperties $retry @('requiredActions', 'source', 'staleEvidenceAction') $artifact '$/retryAdmission'
    Assert-JsonString (Get-JsonProperty $retry 'source' $artifact '$/retryAdmission') $artifact '$/retryAdmission/source' 'exact persisted pending operation, selection, marker evidence, and current Branch evidence'
    Assert-ExactMembers (Get-JsonStringArray (Get-JsonProperty $retry 'requiredActions' $artifact '$/retryAdmission') $artifact '$/retryAdmission/requiredActions') @('reconstructPersistedTypedFacts', 'acquireLocalLease', 'rereadMarkerAndCurrentBranch', 'selectRowFromFreshEvidence') $artifact 'retry admission actions'
    Assert-JsonString (Get-JsonProperty $retry 'staleEvidenceAction' $artifact '$/retryAdmission') $artifact '$/retryAdmission/staleEvidenceAction' 'retainPendingAndDisallowedEvidenceWithoutBranchPublication'
    $order = Get-JsonStringArray (Get-JsonProperty $root 'order' $artifact '$') $artifact '$/order'
    Assert-ExactSequence $order @('sqliteLocalCompletion', 'postCompletionMarkerInspection', 'conditionalExactCleanup', 'typedBranchPublicationOrProof', 'terminalRecording') $artifact 'lifecycle order'

    $rows = Get-JsonProperty $root 'rows' $artifact '$'
    Assert-JsonKind $rows ([System.Text.Json.JsonValueKind]::Array) $artifact '$/rows'
    $index = 0
    foreach ($row in $rows.EnumerateArray()) {
        $location = "$/rows[$index]"
        Assert-JsonKind $row ([System.Text.Json.JsonValueKind]::Object) $artifact $location
        $allowed = @('branchIdentity', 'doctorGuidance', 'durableResult', 'exitClass', 'firstApplicableRetryWrite', 'id', 'match', 'outcome', 'requiredActions', 'resultingMarker', 'workingFiles', 'nextRows')
        $names = @($row.EnumerateObject() | ForEach-Object Name)
        if (@($names | Where-Object { $_ -notin $allowed }).Count -ne 0) { Fail-Contract $artifact "$location has an unknown row property" }
        foreach ($name in @('branchIdentity', 'doctorGuidance', 'durableResult', 'exitClass', 'firstApplicableRetryWrite', 'id', 'match', 'outcome', 'requiredActions', 'workingFiles')) {
            $null = Get-JsonProperty $row $name $artifact $location
        }
        Assert-JsonString (Get-JsonProperty $row 'id' $artifact $location) $artifact "$location/id"
        foreach ($name in @('branchIdentity', 'durableResult', 'exitClass', 'workingFiles')) { Assert-JsonStringOrNull (Get-JsonProperty $row $name $artifact $location) $artifact "$location/$name" }
        Assert-JsonBooleanOrNull (Get-JsonProperty $row 'doctorGuidance' $artifact $location) $artifact "$location/doctorGuidance"
        Assert-JsonString (Get-JsonProperty $row 'firstApplicableRetryWrite' $artifact $location) $artifact "$location/firstApplicableRetryWrite"
        $null = Get-JsonStringArray (Get-JsonProperty $row 'requiredActions' $artifact $location) $artifact "$location/requiredActions"
        $match = Get-JsonProperty $row 'match' $artifact $location
        Assert-JsonExactProperties $match @('invocation', 'marker', 'selectionState', 'trigger') $artifact "$location/match"
        foreach ($axis in $axes) { Assert-JsonPredicate (Get-JsonProperty $match $axis $artifact "$location/match") $axis $artifact "$location/match/$axis" }
        $outcome = Get-JsonProperty $row 'outcome' $artifact $location
        Assert-JsonStringOrNull $outcome $artifact "$location/outcome"
        $next = @($row.EnumerateObject() | Where-Object Name -ceq 'nextRows')
        if ($outcome.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
            if ($next.Count -ne 1 -or @(Get-JsonStringArray $next[0].Value $artifact "$location/nextRows").Count -eq 0) { Fail-Contract $artifact "$location routing row requires nonempty nextRows" }
        } elseif ($next.Count -ne 0) { Fail-Contract $artifact "$location terminal row cannot contain nextRows" }
        $resulting = @($row.EnumerateObject() | Where-Object Name -ceq 'resultingMarker')
        if ($resulting.Count -eq 1) { Assert-JsonString $resulting[0].Value $artifact "$location/resultingMarker" }
        if ($outcome.ValueKind -eq [System.Text.Json.JsonValueKind]::String -and $outcome.GetString() -in @('Unchanged', 'UpdateIncomplete') -and $resulting.Count -ne 0) {
            Fail-Contract $artifact "$location resultingMarker is misplaced for outcome '$($outcome.GetString())'"
        }
        $index++
    }
}

function Test-ProjectionPlanSchema($payload, [string] $artifact) {
    $plan = $payload.Raw
    Assert-JsonExactProperties $plan @('assignments', 'canonicalApplicabilityKeyCount', 'canonicalContentDigest', 'canonicalRowCount', 'schema') $artifact '$/projectionPlan'
    Assert-JsonString (Get-JsonProperty $plan 'schema' $artifact '$/projectionPlan') $artifact '$/projectionPlan/schema' 'grace.wdu.lifecycle-projection-plan/v1'
    Assert-JsonString (Get-JsonProperty $plan 'canonicalContentDigest' $artifact '$/projectionPlan') $artifact '$/projectionPlan/canonicalContentDigest'
    $null = Assert-JsonInteger (Get-JsonProperty $plan 'canonicalRowCount' $artifact '$/projectionPlan') $artifact '$/projectionPlan/canonicalRowCount'
    $null = Assert-JsonInteger (Get-JsonProperty $plan 'canonicalApplicabilityKeyCount' $artifact '$/projectionPlan') $artifact '$/projectionPlan/canonicalApplicabilityKeyCount'
    $assignments = Get-JsonProperty $plan 'assignments' $artifact '$/projectionPlan'
    Assert-JsonKind $assignments ([System.Text.Json.JsonValueKind]::Array) $artifact '$/projectionPlan/assignments'
    $index = 0
    foreach ($assignment in $assignments.EnumerateArray()) {
        Assert-JsonExactProperties $assignment @('artifact', 'proof', 'rowIds') $artifact "$/projectionPlan/assignments[$index]"
        foreach ($name in @('artifact', 'proof')) { Assert-JsonString (Get-JsonProperty $assignment $name $artifact "$/projectionPlan/assignments[$index]") $artifact "$/projectionPlan/assignments[$index]/$name" }
        $null = Get-JsonStringArray (Get-JsonProperty $assignment 'rowIds' $artifact "$/projectionPlan/assignments[$index]") $artifact "$/projectionPlan/assignments[$index]/rowIds"
        $index++
    }
}

function Test-ProjectionSchema($payload, [string] $artifact, [bool] $strict) {
    $projection = $payload.Raw
    $required = @('artifact', 'canonical', 'proof', 'rowIds', 'schema')
    $expected = if ($strict) { @('artifact', 'canonical', 'canonicalContentDigest', 'proof', 'rowIds', 'schema') } else { $null }
    if ($strict) { Assert-JsonExactProperties $projection $expected $artifact '$/projection' }
    else {
        Assert-JsonKind $projection ([System.Text.Json.JsonValueKind]::Object) $artifact '$/projection'
        $names = @($projection.EnumerateObject() | ForEach-Object Name)
        if (@($names | Where-Object { $_ -notin @('artifact', 'canonical', 'canonicalContentDigest', 'proof', 'rowIds', 'schema') }).Count -ne 0 -or
            @($required | Where-Object { $_ -notin $names }).Count -ne 0) { Fail-Contract $artifact 'renderable projection has malformed properties' }
    }
    foreach ($name in @('artifact', 'canonical', 'proof', 'schema')) { Assert-JsonString (Get-JsonProperty $projection $name $artifact '$/projection') $artifact "$/projection/$name" }
    Assert-JsonString (Get-JsonProperty $projection 'schema' $artifact '$/projection') $artifact '$/projection/schema' 'grace.wdu.lifecycle-projection/v1'
    $null = Get-JsonStringArray (Get-JsonProperty $projection 'rowIds' $artifact '$/projection') $artifact '$/projection/rowIds'
    if ($strict) { Assert-JsonString (Get-JsonProperty $projection 'canonicalContentDigest' $artifact '$/projection') $artifact '$/projection/canonicalContentDigest' }
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

function Assert-ExactSequence($actual, [string[]] $expected, [string] $artifact, [string] $label) {
    $actualValues = @($actual)
    if ($actualValues.Count -ne $expected.Count) { Fail-Contract $artifact "$label is not the exact canonical member set or order" }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if ($actualValues[$index] -cne $expected[$index]) { Fail-Contract $artifact "$label is not the exact canonical member set or order at position $index" }
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

function Get-NormalizedLifecycleRow($row, $grammar) {
    $expanded = @{}
    foreach ($axis in @('invocation', 'trigger', 'marker', 'selectionState')) {
        $expanded[$axis] = @(Expand-Predicate $grammar $axis $row.match.$axis $row.id)
    }
    $actions = @($row.requiredActions)
    return [pscustomobject]@{
        Id = $row.id
        Invocation = $expanded.invocation
        Trigger = $expanded.trigger
        Marker = $expanded.marker
        Selection = $expanded.selectionState
        FirstWrite = $row.firstApplicableRetryWrite
        Actions = $actions
        ResultingMarker = if ($null -ne $row.PSObject.Properties['resultingMarker']) { $row.PSObject.Properties['resultingMarker'].Value } else { $null }
        WorkingFiles = $row.workingFiles
        BranchIdentity = $row.branchIdentity
        DurableResult = $row.durableResult
        Outcome = $row.outcome
        ExitClass = $row.exitClass
        DoctorGuidance = $row.doctorGuidance
        NextRows = if ($null -ne $row.PSObject.Properties['nextRows']) { @($row.PSObject.Properties['nextRows'].Value) } else { @() }
        IsRouting = $null -eq $row.outcome
        PublicationEffects = @($actions | Where-Object { $_ -in @('publishSelectedBranch', 'attemptPublishSelectedBranch') })
        TerminalEffects = @($actions | Where-Object { $_ -in @('recordTerminal', 'attemptTerminalRecording') })
    }
}

function Test-LifecycleRowSemantics($normalizedRows, [string] $artifact) {
    $disallowedMarkers = @('differentOperation', 'malformed', 'unsupported', 'unreadable', 'exactCleanupFailed')
    $outcomeDurableResult = @{ FinalizationIncomplete = 'pending'; Rejected = 'noCompletion'; Unchanged = 'existingTerminal'; Updated = 'terminal'; UpdateIncomplete = 'noCompletion' }
    foreach ($row in $normalizedRows) {
        if (@($row.PublicationEffects).Count -gt 1) { Fail-Contract $row.Id 'row contains more than one Branch publication effect' }
        if (@($row.TerminalEffects).Count -gt 1) { Fail-Contract $row.Id 'row contains more than one terminal effect' }
        if ($row.IsRouting -and @($row.NextRows).Count -eq 0) { Fail-Contract $row.Id 'routing row has no nextRows' }
        if (-not $row.IsRouting -and @($row.NextRows).Count -ne 0) { Fail-Contract $row.Id 'terminal row cannot route to nextRows' }
        if (-not $row.IsRouting -and $null -eq $outcomeDurableResult[$row.Outcome]) { Fail-Contract $row.Id "unknown outcome '$($row.Outcome)'" }
        if (-not $row.IsRouting -and $row.DurableResult -ne $outcomeDurableResult[$row.Outcome]) { Fail-Contract $row.Id "outcome '$($row.Outcome)' requires durableResult '$($outcomeDurableResult[$row.Outcome])'" }
        if ($row.Outcome -eq 'FinalizationIncomplete' -and ($row.ExitClass -ne 'nonzero' -or $row.DoctorGuidance -ne $true)) {
            Fail-Contract $row.Id 'FinalizationIncomplete lacks nonzero exit or Doctor guidance'
        }

        $isPreLocalRefusal = $row.Trigger -contains 'preLocalAdmissionRefused'
        $isPostLocalDisallowed = $row.Invocation -contains 'initial' -and $row.Trigger -contains 'afterSqliteLocalCompletion' -and
            @($row.Marker | Where-Object { $_ -in @('differentOperation', 'malformed', 'unsupported', 'unreadable') }).Count -gt 0
        $isRetryDisallowed = $row.Invocation -contains 'finalizationRetry' -and $row.Trigger -contains 'disallowedMarker'
        if ($isPreLocalRefusal) {
            if ($row.Actions -notcontains 'retainMarkerEvidence' -or $row.Actions -contains 'retainPending' -or
                $row.Outcome -ne 'Rejected' -or $row.DurableResult -ne 'noCompletion' -or $row.FirstWrite -ne 'none' -or
                $row.WorkingFiles -ne 'unchanged' -or $row.BranchIdentity -ne 'unchanged') {
                Fail-Contract $row.Id 'pre-local disallowed admission has incoherent evidence, result, or mutation boundary'
            }
        }
        if ($isPostLocalDisallowed) {
            if ((@($row.Actions | Where-Object { $_ -in @('retainMarker', 'retainEvidence') }).Count -eq 0) -or $row.Actions -notcontains 'retainPending' -or
                $row.Outcome -ne 'FinalizationIncomplete' -or $row.DurableResult -ne 'pending' -or $row.FirstWrite -ne 'none' -or
                $row.WorkingFiles -ne 'verifiedTarget' -or $row.BranchIdentity -ne 'unchanged') {
                Fail-Contract $row.Id 'post-local disallowed evidence has incoherent retention, result, or mutation boundary'
            }
        }
        if ($isRetryDisallowed) {
            if ($row.Actions -notcontains 'retainEvidence' -or $row.Actions -notcontains 'retainPending' -or
                $row.Outcome -ne 'FinalizationIncomplete' -or $row.DurableResult -ne 'pending' -or $row.FirstWrite -ne 'none' -or
                $row.WorkingFiles -ne 'unchanged' -or $row.BranchIdentity -ne 'unchanged') {
                Fail-Contract $row.Id 'retry disallowed evidence has incoherent retention, result, or mutation boundary'
            }
        }

        $hasDisallowedMarker = @($row.Marker | Where-Object { $_ -in $disallowedMarkers }).Count -gt 0
        if ($hasDisallowedMarker -and $row.Invocation -contains 'finalizationRetry') {
            foreach ($required in @('retainEvidence', 'retainPending')) {
                if ($row.Actions -notcontains $required) { Fail-Contract $row.Id "disallowed marker cell must retain '$required'" }
            }
            if (@($row.PublicationEffects).Count -ne 0 -or @($row.TerminalEffects).Count -ne 0) {
                Fail-Contract $row.Id 'disallowed marker cell cannot attempt or complete publication or terminal recording'
            }
            if ($row.DurableResult -ne 'pending' -or $row.Outcome -ne 'FinalizationIncomplete' -or $row.DoctorGuidance -ne $true -or
                $row.WorkingFiles -ne 'unchanged' -or $row.BranchIdentity -ne 'unchanged' -or $row.FirstWrite -ne 'none') {
                Fail-Contract $row.Id 'disallowed marker cell has incoherent pending result, outcome, guidance, or retry cutoff'
            }
        }

        if ($hasDisallowedMarker -and (@($row.PublicationEffects).Count -ne 0 -or @($row.TerminalEffects).Count -ne 0)) {
            Fail-Contract $row.Id 'disallowed marker cell cannot attempt or complete publication or terminal recording'
        }
        if (@($row.PublicationEffects).Count -eq 1) {
            if ($row.Selection.Count -ne 1 -or $row.Selection[0] -ne 'referencePrevious' -or $hasDisallowedMarker) {
                Fail-Contract $row.Id 'Branch publication is only legal for Reference previous-Branch finalization without disallowed evidence'
            }
        }
        if (@($row.TerminalEffects).Count -eq 1) {
            if ($row.Selection.Count -ne 1) { Fail-Contract $row.Id 'terminal recording requires one concrete selection family' }
            $proof = switch ($row.Selection[0]) {
                'referencePrevious' { 'provePublication' }
                'referenceSelected' { 'proveSelectedBranch' }
                'directoryVersion' { 'proveCurrentBranchUnchanged' }
                default { $null }
            }
            $terminalIndex = [array]::IndexOf($row.Actions, @($row.TerminalEffects)[0])
            $proofIndex = if ($proof) { [array]::IndexOf($row.Actions, $proof) } else { -1 }
            if ($proofIndex -lt 0 -or $proofIndex -ge $terminalIndex) { Fail-Contract $row.Id 'terminal recording requires prior durable identity proof' }
        }
        if ($row.FirstWrite -eq 'terminalRecording' -and @($row.Selection | Where-Object { $_ -in @('referenceSelected', 'directoryVersion') }).Count -ne $row.Selection.Count) {
            Fail-Contract $row.Id 'terminal recording cutoff requires selected Reference or DirectoryVersion'
        }
        if ($row.Marker -contains 'exact' -and (@($row.PublicationEffects).Count -eq 1 -or @($row.TerminalEffects).Count -eq 1)) {
            $cleanup = [array]::IndexOf($row.Actions, 'cleanExactMarker')
            $effect = if (@($row.PublicationEffects).Count -eq 1) { [array]::IndexOf($row.Actions, @($row.PublicationEffects)[0]) } else { [array]::IndexOf($row.Actions, @($row.TerminalEffects)[0]) }
            if ($cleanup -lt 0 -or $cleanup -ge $effect) { Fail-Contract $row.Id 'exact cleanup must precede publication or terminal effect' }
        }
        if ($row.Outcome -eq 'Updated' -and $row.DurableResult -eq 'terminal') {
            if ($row.Selection.Count -ne 1 -or $row.Actions -notcontains 'recordTerminal' -or $row.ExitClass -ne 'success' -or $row.DoctorGuidance -ne $false) {
                Fail-Contract $row.Id 'successful terminal row lacks one selected identity, terminal record, success exit, or no Doctor guidance'
            }
            switch ($row.Selection[0]) {
                'referencePrevious' {
                    if ($row.Actions -notcontains 'publishSelectedBranch' -or $row.Actions -notcontains 'provePublication' -or $row.BranchIdentity -ne 'selected') {
                        Fail-Contract $row.Id 'successful previous-Reference publication must end on the selected Branch'
                    }
                }
                'referenceSelected' {
                    if ($row.Actions -notcontains 'proveSelectedBranch' -or $row.BranchIdentity -ne 'selected') {
                        Fail-Contract $row.Id 'successful selected-Reference finalization must end on the selected Branch'
                    }
                }
                'directoryVersion' {
                    if ($row.Actions -notcontains 'proveCurrentBranchUnchanged' -or $row.BranchIdentity -ne 'currentUnchanged') {
                        Fail-Contract $row.Id 'successful DirectoryVersion proof must leave the current Branch unchanged'
                    }
                }
                'referenceThird' { Fail-Contract $row.Id 'third Branch cannot reach a successful terminal row' }
            }
        }
        if ($row.Selection -contains 'referenceThird' -and $row.Outcome -eq 'Updated') { Fail-Contract $row.Id 'third Branch cannot reach a successful terminal row' }
        if ($row.Outcome -eq 'Unchanged' -and ($row.Actions.Count -ne 0 -or $row.ResultingMarker -or $row.FirstWrite -ne 'none' -or
                $row.WorkingFiles -ne 'unchanged' -or $row.BranchIdentity -ne 'unchanged' -or $row.DurableResult -ne 'existingTerminal')) {
            Fail-Contract $row.Id 'unchanged terminal replay must be mutation-free and coherent'
        }
        if ($row.Invocation -contains 'terminalReplay' -and ($row.Outcome -ne 'Unchanged' -or $row.ExitClass -ne 'success' -or $row.DoctorGuidance -ne $false)) {
            Fail-Contract $row.Id 'terminal replay must be success without Doctor guidance'
        }
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

    $normalizedRows = @($rows | ForEach-Object { Get-NormalizedLifecycleRow $_ $grammar })
    Test-LifecycleRowSemantics $normalizedRows $artifact
    $terminalReplays = @($normalizedRows | Where-Object { $_.Invocation -contains 'terminalReplay' })
    if ($terminalReplays.Count -ne 1) { Fail-Contract $artifact 'expected one terminal replay row' }

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
    $payload = Get-FencedJsonPayload $block.Content $canonicalArtifact 'grace.wdu.lifecycle-projection-plan/v1'
    Test-ProjectionPlanSchema $payload $canonicalArtifact
    $plan = $payload.Value
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
    $payload = Get-FencedJsonPayload $block.Content $artifact 'grace.wdu.lifecycle-projection/v1'
    Test-ProjectionSchema $payload $artifact $strict
    $projection = $payload.Value
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
        $history = ($outside.Substring($historyStart, $historyEnd - $historyStart) -replace "`r`n|`r", "`n").Trim()
        # History is generated evidence, not a prose exception.  Its one permitted rendered form is intentionally exact.
        if ($history -cne 'Historical supersession reference: [PR #873](https://github.com/ScottArbeit/Grace/pull/873).') {
            Fail-Contract $path 'historical evidence must be the exact generated PR #873 reference'
        }
        $outside = $outside.Remove($historyEnds[0].Index, $historyEnds[0].Length).Remove($historyStarts[0].Index, $historyStarts[0].Length)
        $outside = $outside.Remove($historyStarts[0].Index, $historyEnd - $historyStart)
    }
    Test-CompetingLifecycleSource $outside $path
}

function Test-CompetingLifecycleSource([string] $outside, [string] $path) {
    $sequence = $outside -replace "`r`n|`r", "`n"
    $sequence = $sequence -replace '`([^`]*)`', '$1' -replace '<[^>]+>', ''
    $sequence = $sequence -replace '\[([^\]]+)\]\([^\)]+\)', '$1' -replace '\[([^\]]+)\]\[[^\]]*\]', '$1'
    $sequence = $sequence -replace '\\([\\`*_{}\[\]()#+.!-])', '$1' -replace '[*_]', '' -replace '\s+', ' '
    $sentences = @($sequence -split '(?<=[.!?])\s+' | Where-Object { $_.Trim() })
    $cleanup = '(?:(?:clean(?:up)?|remove)\s+(?:the\s+)?(?:exact\s+)?(?:owned\s+)?marker|(?:exact\s+)?marker\s+cleanup)'
    $publication = '(?:(?:attempt\s+to\s+)?publish(?:ing|ed)?\s+(?:the\s+)?(?:selected\s+)?Branch|Branch\s+publication)'
    $terminal = '(?:record(?:ing)?\s+(?:terminal\s+)?(?:completion|recording)|terminal\s+completion)'
    $proof = '(?:prove\s+(?:publication|(?:the\s+)?selected\s+Branch|(?:the\s+)?current\s+Branch\s+(?:is\s+)?unchanged)|proof\s+of\s+(?:publication|selected\s+Branch|current\s+Branch))'
    $continuedRules = @($sentences)
    for ($index = 0; $index -lt ($sentences.Count - 1); $index++) { $continuedRules += "$($sentences[$index]) $($sentences[$index + 1])" }
    foreach ($sentence in $continuedRules) {
        # A source rule must assert an effect or ordering; explanations of rejection/comparison are not operational rules.
        if ($sentence -match '(?i)\b(rejects?|forbids?|must not|cannot|distinguishes?)\b') { continue }
        if ($sentence -match "(?is)$publication.{0,240}(?:before|then|after).{0,160}$cleanup" -or
            $sentence -match "(?is)$publication.{0,240}$cleanup") {
            Fail-Contract $path 'contains competing lifecycle source outside its projection: publication precedes exact cleanup'
        }
        if ($sentence -match "(?is)$cleanup.{0,260}$publication.{0,260}$proof.{0,260}$terminal") {
            Fail-Contract $path 'contains competing lifecycle source outside its projection: copied cleanup/publication/proof/terminal sequence'
        }
        if ($sentence -match "(?is)$proof.{0,160}(?:before|then|after|and\s+then|,|and).{0,120}$terminal") {
            Fail-Contract $path 'contains competing lifecycle source outside its projection: Branch proof/terminal sequence'
        }
        if ($sentence -match '(?is)retry.{0,160}first\s+applicable\s+write.{0,160}(?:is|before|then|must).{0,160}(?:exact\s+cleanup|Branch\s+publication|terminal\s+recording)') {
            Fail-Contract $path 'contains competing lifecycle source outside its projection: retry first-write sequence'
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
Test-LifecycleSchema $lifecyclePayload $canonical.Path
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
