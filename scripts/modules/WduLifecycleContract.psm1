Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:StartMarker = '<!-- grace:wdu-lifecycle-contract:start -->'
$script:EndMarker = '<!-- grace:wdu-lifecycle-contract:end -->'

function Fail-Contract {
    param([string] $Path, [string] $Reason)
    throw "WDU lifecycle contract '$Path': $Reason"
}

function Test-OrdinalStringEquals {
    param([string] $Left, [string] $Right)
    return [StringComparer]::Ordinal.Equals($Left, $Right)
}

function Test-OrdinalIgnoreCaseStringEquals {
    param([string] $Left, [string] $Right)
    return [StringComparer]::OrdinalIgnoreCase.Equals($Left, $Right)
}

function Test-OrdinalStringInCollection {
    param([string[]] $Values, [string] $Expected)
    foreach ($value in $Values) {
        if (Test-OrdinalStringEquals $value $Expected) { return $true }
    }
    return $false
}

function Find-OrdinalStringIndex {
    param([string[]] $Values, [string] $Expected)
    for ($index = 0; $index -lt $Values.Count; $index++) {
        if (Test-OrdinalStringEquals $Values[$index] $Expected) { return $index }
    }
    return -1
}

function Test-ExactDictionaryKey {
    param([System.Collections.IDictionary] $Value, [string] $Name)
    foreach ($key in $Value.Keys) {
        if (Test-OrdinalStringEquals ([string] $key) $Name) { return $true }
    }
    return $false
}

function Assert-AllowedObjectProperties {
    param([object] $Value, [string[]] $AllowedNames, [string[]] $RequiredNames, [string] $Path, [string] $SourcePath)
    if ($Value -isnot [System.Collections.IDictionary]) { Fail-Contract $SourcePath "$Path must be a JSON object" }
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $AllowedNames) { $null = $allowed.Add($name) }
    foreach ($name in $Value.Keys) {
        if (-not $allowed.Contains([string] $name)) { Fail-Contract $SourcePath "$Path has unknown property '$name'" }
    }
    foreach ($name in $RequiredNames) {
        if (-not (Test-ExactDictionaryKey $Value $name)) { Fail-Contract $SourcePath "$Path is missing property '$name'" }
    }
}

function Assert-ExactObject {
    param([object] $Value, [string[]] $Names, [string] $Path, [string] $SourcePath)
    Assert-AllowedObjectProperties $Value $Names $Names $Path $SourcePath
}

function Assert-StringValue {
    param([object] $Value, [string] $Path, [string] $SourcePath)
    if ($Value -isnot [string]) { Fail-Contract $SourcePath "$Path must be a JSON string" }
}

function Assert-StringArray {
    param([object] $Value, [string] $Path, [string] $SourcePath, [bool] $AllowEmpty = $false)
    if ($Value -is [string] -or $Value -isnot [System.Collections.IEnumerable] -or $Value -is [System.Collections.IDictionary]) {
        Fail-Contract $SourcePath "$Path must be a JSON array of strings"
    }
    $values = @($Value)
    if (-not $AllowEmpty -and $values.Count -eq 0) { Fail-Contract $SourcePath "$Path must not be empty" }
    foreach ($item in $values) { Assert-StringValue $item "$Path[]" $SourcePath }
    return $values
}

function Assert-UniqueStrings {
    param([string[]] $Values, [string] $Path, [string] $SourcePath)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Values) {
        if (-not $seen.Add($value)) { Fail-Contract $SourcePath "$Path contains duplicate value '$value'" }
    }
}

function Assert-NullableString {
    param([object] $Value, [string] $Path, [string] $SourcePath)
    if ($null -ne $Value) { Assert-StringValue $Value $Path $SourcePath }
}

function Assert-NullableBoolean {
    param([object] $Value, [string] $Path, [string] $SourcePath)
    if ($null -ne $Value -and $Value -isnot [bool]) { Fail-Contract $SourcePath "$Path must be a JSON boolean or null" }
}

function Assert-NoDuplicateProperties {
    param([System.Text.Json.JsonElement] $Element, [string] $Path, [string] $SourcePath)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { Fail-Contract $SourcePath "$Path has duplicate case-equivalent property '$($property.Name)'" }
            Assert-NoDuplicateProperties $property.Value "$Path.$($property.Name)" $SourcePath
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateProperties $item "$Path[$index]" $SourcePath
            $index++
        }
    }
}

function Get-DeclaredEnumValues {
    param([System.Collections.IDictionary] $Enums, [string] $Name, [string] $SourcePath)
    if (-not (Test-ExactDictionaryKey $Enums $Name)) { Fail-Contract $SourcePath "machineGrammar.concreteEnums is missing '$Name'" }
    return @(Assert-StringArray $Enums[$Name] "machineGrammar.concreteEnums.$Name" $SourcePath)
}

function Expand-Predicate {
    param(
        [object] $Predicate,
        [string] $Axis,
        [System.Collections.IDictionary] $ConcreteEnums,
        [System.Collections.IDictionary] $Aggregates,
        [string] $RowPath,
        [string] $SourcePath
    )
    if ($Predicate -isnot [System.Collections.IDictionary]) {
        Fail-Contract $SourcePath "$RowPath.$Axis must be a predicate object with kind"
    }
    if (-not (Test-ExactDictionaryKey $Predicate 'kind')) {
        foreach ($name in $Predicate.Keys) {
            if (Test-OrdinalIgnoreCaseStringEquals ([string] $name) 'kind') { Fail-Contract $SourcePath "$RowPath.$Axis has unknown property '$name'" }
        }
        Fail-Contract $SourcePath "$RowPath.$Axis must be a predicate object with kind"
    }
    $values = Get-DeclaredEnumValues $ConcreteEnums $Axis $SourcePath
    $kind = $Predicate['kind']; Assert-StringValue $kind "$RowPath.$Axis.kind" $SourcePath
    if (Test-OrdinalStringEquals $kind 'one') {
        Assert-ExactObject $Predicate @('kind', 'value') "$RowPath.$Axis" $SourcePath
        Assert-StringValue $Predicate['value'] "$RowPath.$Axis.value" $SourcePath
        $expanded = @($Predicate['value'])
    }
    elseif (Test-OrdinalStringEquals $kind 'set') {
        Assert-ExactObject $Predicate @('kind', 'values') "$RowPath.$Axis" $SourcePath
        $expanded = @(Assert-StringArray $Predicate['values'] "$RowPath.$Axis.values" $SourcePath)
        Assert-UniqueStrings $expanded "$RowPath.$Axis.values" $SourcePath
    }
    elseif (Test-OrdinalStringEquals $kind 'aggregate') {
        Assert-ExactObject $Predicate @('kind', 'name') "$RowPath.$Axis" $SourcePath
        Assert-StringValue $Predicate['name'] "$RowPath.$Axis.name" $SourcePath
        if (-not (Test-ExactDictionaryKey $Aggregates $Axis) -or $Aggregates[$Axis] -isnot [System.Collections.IDictionary]) {
            Fail-Contract $SourcePath "$RowPath.$Axis aggregate is not declared for this axis"
        }
        $axisAggregates = $Aggregates[$Axis]
        if (-not (Test-ExactDictionaryKey $axisAggregates $Predicate['name'])) {
            Fail-Contract $SourcePath "$RowPath.$Axis aggregate '$($Predicate['name'])' is not declared"
        }
        $expanded = @(Assert-StringArray $axisAggregates[$Predicate['name']] "machineGrammar.aggregates.$Axis.$($Predicate['name'])" $SourcePath)
        Assert-UniqueStrings $expanded "machineGrammar.aggregates.$Axis.$($Predicate['name'])" $SourcePath
    }
    else { Fail-Contract $SourcePath "$RowPath.$Axis has unknown predicate kind '$kind'" }
    foreach ($value in $expanded) {
        if (-not (Test-OrdinalStringInCollection $values $value)) { Fail-Contract $SourcePath "$RowPath.$Axis value '$value' is not declared" }
    }
    return $expanded
}

function Assert-VerifiedRootCorrection {
    param([Collections.Generic.Dictionary[string, object]] $RowsById, [string] $SourcePath)

    function Get-CorrectionRow {
        param([string] $RowId)
        if (-not $RowsById.ContainsKey($RowId)) { Fail-Contract $SourcePath "missing required verified-root row '$RowId'" }
        return $RowsById[$RowId]
    }

    function Assert-CorrectionAction {
        param([object] $Row, [string] $RowId, [string] $Action)
        if (-not (Test-OrdinalStringInCollection @($Row['requiredActions']) $Action)) {
            Fail-Contract $SourcePath "row '$RowId' must require '$Action'"
        }
    }

    function Assert-CorrectionNextRow {
        param([object] $Row, [string] $RowId, [string] $Target)
        if (-not (Test-ExactDictionaryKey $Row 'nextRows') -or -not (Test-OrdinalStringInCollection @($Row['nextRows']) $Target)) {
            Fail-Contract $SourcePath "row '$RowId' must route to '$Target'"
        }
    }

    $fresh = Get-CorrectionRow 'WDU-LC-200'
    $adopted = Get-CorrectionRow 'WDU-LC-201'
    $preTransition = Get-CorrectionRow 'WDU-LC-209'
    $mutation = Get-CorrectionRow 'WDU-LC-210'
    $mutationFailure = Get-CorrectionRow 'WDU-LC-002'
    $completion = Get-CorrectionRow 'WDU-LC-006'
    $completionFailure = Get-CorrectionRow 'WDU-LC-007'

    Assert-CorrectionAction $fresh 'WDU-LC-200' 'reconcileFreshAdmissionAsNeedsApplyOnly'
    Assert-CorrectionAction $adopted 'WDU-LC-201' 'reconcileExactAdoptionAsNeedsApplyOrAlreadySatisfied'
    Assert-CorrectionAction $preTransition 'WDU-LC-209' 'compareCompleteRelevantTopologyWithPrefixAdvancedExpectedState'
    Assert-CorrectionAction $preTransition 'WDU-LC-209' 'checkCancellationImmediatelyBeforeVerifiedLocalRootOrFirstMutation'
    Assert-CorrectionAction $preTransition 'WDU-LC-209' 'routeZeroActionToVerifiedLocalRootOrMutatingPlanToFirstAction'
    foreach ($target in @('WDU-LC-207', 'WDU-LC-208', 'WDU-LC-210', 'WDU-LC-006', 'WDU-LC-007')) { Assert-CorrectionNextRow $preTransition 'WDU-LC-209' $target }
    Assert-CorrectionAction $mutation 'WDU-LC-210' 'compareCompleteRelevantTopologyWithPrefixAdvancedExpectedStateBeforeEveryLaterAction'
    Assert-CorrectionAction $mutation 'WDU-LC-210' 'transitionToVerifiedLocalRoot'
    foreach ($target in @('WDU-LC-002', 'WDU-LC-006', 'WDU-LC-007')) { Assert-CorrectionNextRow $mutation 'WDU-LC-210' $target }

    if (-not (Test-OrdinalStringEquals $mutationFailure['match']['trigger']['value'] 'failureAfterFirstWorkingTreeMutationBeforeVerifiedLocalRoot')) {
        Fail-Contract $SourcePath "row 'WDU-LC-002' must be limited to post-mutation pre-VerifiedLocalRoot failure"
    }
    Assert-CorrectionAction $completion 'WDU-LC-006' 'ignoreCancellation'
    Assert-CorrectionAction $completion 'WDU-LC-006' 'returnLocalCompletionWithEphemeralBytesChanged'
    if (-not (Test-OrdinalStringEquals $completionFailure['match']['trigger']['value'] 'failureAfterVerifiedLocalRootBeforeSqliteLocalCompletion') -or
        -not (Test-OrdinalStringEquals $completionFailure['outcome'] 'UpdateIncomplete') -or
        -not (Test-OrdinalStringEquals $completionFailure['durableResult'] 'noCompletion')) {
        Fail-Contract $SourcePath "row 'WDU-LC-007' must classify post-VerifiedLocalRoot completion failure as retained-evidence UpdateIncomplete"
    }
    Assert-CorrectionAction $completionFailure 'WDU-LC-007' 'ignoreCancellation'
    Assert-CorrectionAction $completionFailure 'WDU-LC-007' 'retainExactMarkerEvidence'
}

function Assert-EphemeralBytesChangedTerminalization {
    param([Collections.Generic.Dictionary[string, object]] $RowsById, [string] $SourcePath)

    function Get-BytesChangedRow {
        param([string] $RowId)
        if (-not $RowsById.ContainsKey($RowId)) {
            Fail-Contract $SourcePath "missing required bytesChanged terminal row '$RowId'"
        }
        return $RowsById[$RowId]
    }

    function Assert-BytesChangedTerminal {
        param([object] $Row, [string] $RowId, [string] $Trigger, [string] $Marker, [string] $Outcome)
        if (-not (Test-OrdinalStringEquals $Row['match']['invocation']['value'] 'initial') -or
            -not (Test-OrdinalStringEquals $Row['match']['selectionState']['value'] 'directoryVersion') -or
            -not (Test-OrdinalStringEquals $Row['match']['trigger']['value'] $Trigger) -or
            -not (Test-OrdinalStringEquals $Row['match']['marker']['value'] $Marker) -or
            -not (Test-OrdinalStringEquals $Row['outcome'] $Outcome)) {
            Fail-Contract $SourcePath "row '$RowId' must be the DirectoryVersion '$Trigger' '$Marker' terminal with outcome '$Outcome'"
        }
        if (-not (Test-OrdinalStringInCollection @($Row['requiredActions']) 'recordTerminal')) {
            Fail-Contract $SourcePath "row '$RowId' must require 'recordTerminal'"
        }
    }

    $changedMissing = Get-BytesChangedRow 'WDU-LC-026'
    $unchangedMissing = Get-BytesChangedRow 'WDU-LC-028'
    $changedExact = Get-BytesChangedRow 'WDU-LC-036'
    $unchangedExact = Get-BytesChangedRow 'WDU-LC-038'
    Assert-BytesChangedTerminal $changedMissing 'WDU-LC-026' 'afterSqliteLocalCompletionBytesChanged' 'missing' 'Updated'
    Assert-BytesChangedTerminal $unchangedMissing 'WDU-LC-028' 'afterSqliteLocalCompletionBytesUnchanged' 'missing' 'Unchanged'
    Assert-BytesChangedTerminal $changedExact 'WDU-LC-036' 'afterSqliteLocalCompletionBytesChanged' 'exact' 'Updated'
    Assert-BytesChangedTerminal $unchangedExact 'WDU-LC-038' 'afterSqliteLocalCompletionBytesUnchanged' 'exact' 'Unchanged'

    $completion = Get-BytesChangedRow 'WDU-LC-006'
    foreach ($target in @('WDU-LC-026', 'WDU-LC-028', 'WDU-LC-036', 'WDU-LC-038')) {
        if (-not (Test-OrdinalStringInCollection @($completion['nextRows']) $target)) {
            Fail-Contract $SourcePath "row 'WDU-LC-006' must route to '$target'"
        }
    }

    foreach ($referenceRowId in @('WDU-LC-020', 'WDU-LC-023', 'WDU-LC-025', 'WDU-LC-030', 'WDU-LC-033', 'WDU-LC-035')) {
        $referenceRow = Get-BytesChangedRow $referenceRowId
        if (-not (Test-OrdinalStringEquals $referenceRow['match']['trigger']['value'] 'afterSqliteLocalCompletion')) {
            Fail-Contract $SourcePath "Reference row '$referenceRowId' must remain on afterSqliteLocalCompletion"
        }
    }
}

function Read-WduLifecycleContract {
    param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Path)

    $sourcePath = [IO.Path]::GetFullPath($Path)
    $text = [IO.File]::ReadAllText($sourcePath)
    $lines = @($text -split "`r`n|`n|`r")
    $starts = @($lines | Where-Object { Test-OrdinalStringEquals $_ $script:StartMarker })
    $ends = @($lines | Where-Object { Test-OrdinalStringEquals $_ $script:EndMarker })
    if ($starts.Count -ne 1 -or $ends.Count -ne 1) { Fail-Contract $sourcePath 'requires one exact lifecycle marker pair' }
    $start = Find-OrdinalStringIndex $lines $script:StartMarker
    $end = Find-OrdinalStringIndex $lines $script:EndMarker
    if ($start -ge $end) { Fail-Contract $sourcePath 'lifecycle markers are reversed' }
    $marked = [string]::Join("`n", @($lines[($start + 1)..($end - 1)]))
    $match = [regex]::Match($marked, '(?s)\A\s*```json[ \t]*\n(?<json>\{.*\})\n```[ \t]*\s*\z')
    if (-not $match.Success) { Fail-Contract $sourcePath 'marked payload must be one fenced JSON object' }
    $json = $match.Groups['json'].Value.Replace("`r`n", "`n").Replace("`r", "`n")
    try { $document = [Text.Json.JsonDocument]::Parse($json) }
    catch { Fail-Contract $sourcePath "marked JSON is malformed: $($_.Exception.Message)" }
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { Fail-Contract $sourcePath 'marked payload must be a JSON object' }
        Assert-NoDuplicateProperties $document.RootElement '$' $sourcePath
        $rawRows = @($document.RootElement.EnumerateObject() | Where-Object { Test-OrdinalStringEquals $_.Name 'rows' })
        if ($rawRows.Count -eq 1 -and $rawRows[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
            Fail-Contract $sourcePath 'rows must be a JSON array'
        }
    }
    finally { $document.Dispose() }
    try { $contract = $json | ConvertFrom-Json -AsHashtable -Depth 100 }
    catch { Fail-Contract $sourcePath "marked JSON cannot be decoded: $($_.Exception.Message)" }

    Assert-ExactObject $contract @('schema', 'boundaries', 'retryAdmission', 'doctorCommand', 'order', 'machineGrammar', 'rows') '$' $sourcePath
    if (-not (Test-OrdinalStringEquals $contract['schema'] 'grace.wdu.branch-lifecycle/v1')) { Fail-Contract $sourcePath 'schema must be grace.wdu.branch-lifecycle/v1' }
    Assert-ExactObject $contract['boundaries'] @('firstWorkingTreeMutation', 'verifiedLocalRoot', 'sqliteLocalCompletion', 'firstApplicableRetryWrite') 'boundaries' $sourcePath
    foreach ($name in $contract['boundaries'].Keys) { Assert-StringValue $contract['boundaries'][$name] "boundaries.$name" $sourcePath }
    Assert-ExactObject $contract['retryAdmission'] @('source', 'requiredActions', 'staleEvidenceAction') 'retryAdmission' $sourcePath
    Assert-StringValue $contract['retryAdmission']['source'] 'retryAdmission.source' $sourcePath
    Assert-StringArray $contract['retryAdmission']['requiredActions'] 'retryAdmission.requiredActions' $sourcePath | Out-Null
    Assert-StringValue $contract['retryAdmission']['staleEvidenceAction'] 'retryAdmission.staleEvidenceAction' $sourcePath
    Assert-StringValue $contract['doctorCommand'] 'doctorCommand' $sourcePath
    Assert-StringArray $contract['order'] 'order' $sourcePath | Out-Null

    $grammar = $contract['machineGrammar']
    Assert-ExactObject $grammar @('predicateAxes', 'encoding', 'concreteEnums', 'aggregates', 'expansion', 'overlap', 'terminalReplay') 'machineGrammar' $sourcePath
    $axes = @(Assert-StringArray $grammar['predicateAxes'] 'machineGrammar.predicateAxes' $sourcePath); Assert-UniqueStrings $axes 'machineGrammar.predicateAxes' $sourcePath
    Assert-ExactObject $grammar['encoding'] @('one', 'set', 'aggregate') 'machineGrammar.encoding' $sourcePath
    foreach ($kind in @('one', 'set', 'aggregate')) {
        Assert-ExactObject $grammar['encoding'][$kind] @('jsonShape', 'meaning') "machineGrammar.encoding.$kind" $sourcePath
        $shapeNames = if (Test-OrdinalStringEquals $kind 'one') { @('kind', 'value') } elseif (Test-OrdinalStringEquals $kind 'set') { @('kind', 'values') } else { @('kind', 'name') }
        $shape = $grammar['encoding'][$kind]['jsonShape']
        Assert-ExactObject $shape $shapeNames "machineGrammar.encoding.$kind.jsonShape" $sourcePath
        Assert-StringValue $shape['kind'] "machineGrammar.encoding.$kind.jsonShape.kind" $sourcePath
        if (-not (Test-OrdinalStringEquals $shape['kind'] $kind)) { Fail-Contract $sourcePath "machineGrammar.encoding.$kind must declare kind '$kind'" }
        if (Test-OrdinalStringEquals $kind 'set') { Assert-StringArray $shape['values'] "machineGrammar.encoding.$kind.jsonShape.values" $sourcePath | Out-Null }
        else { Assert-StringValue $shape[$shapeNames[1]] "machineGrammar.encoding.$kind.jsonShape.$($shapeNames[1])" $sourcePath }
        Assert-StringValue $grammar['encoding'][$kind]['meaning'] "machineGrammar.encoding.$kind.meaning" $sourcePath
    }
    $enums = $grammar['concreteEnums']; if ($enums -isnot [System.Collections.IDictionary]) { Fail-Contract $sourcePath 'machineGrammar.concreteEnums must be an object' }
    foreach ($name in $enums.Keys) { $members = @(Assert-StringArray $enums[$name] "machineGrammar.concreteEnums.$name" $sourcePath); Assert-UniqueStrings $members "machineGrammar.concreteEnums.$name" $sourcePath }
    foreach ($axis in $axes) { Get-DeclaredEnumValues $enums $axis $sourcePath | Out-Null }
    $aggregates = $grammar['aggregates']; if ($aggregates -isnot [System.Collections.IDictionary]) { Fail-Contract $sourcePath 'machineGrammar.aggregates must be an object' }
    foreach ($axis in $aggregates.Keys) {
        if (-not (Test-OrdinalStringInCollection $axes $axis) -or $aggregates[$axis] -isnot [System.Collections.IDictionary]) { Fail-Contract $sourcePath "machineGrammar.aggregates.$axis is not an axis aggregate object" }
        foreach ($name in $aggregates[$axis].Keys) {
            $members = @(Assert-StringArray $aggregates[$axis][$name] "machineGrammar.aggregates.$axis.$name" $sourcePath)
            Assert-UniqueStrings $members "machineGrammar.aggregates.$axis.$name" $sourcePath
            $declared = Get-DeclaredEnumValues $enums $axis $sourcePath
            foreach ($member in $members) { if (-not (Test-OrdinalStringInCollection $declared $member)) { Fail-Contract $sourcePath "machineGrammar.aggregates.$axis.$name has undeclared member '$member'" } }
        }
    }
    Assert-ExactObject $grammar['expansion'] @('rule', 'setMembers', 'aggregateMembers', 'example') 'machineGrammar.expansion' $sourcePath
    Assert-ExactObject $grammar['overlap'] @('applicabilityKey', 'rule', 'routing') 'machineGrammar.overlap' $sourcePath
    $keyAxes = @(Assert-StringArray $grammar['overlap']['applicabilityKey'] 'machineGrammar.overlap.applicabilityKey' $sourcePath)
    if ($axes.Count -ne $keyAxes.Count) { Fail-Contract $sourcePath 'machineGrammar.overlap.applicabilityKey must name the predicate axes in declared order' }
    for ($index = 0; $index -lt $axes.Count; $index++) {
        if (-not (Test-OrdinalStringEquals $axes[$index] $keyAxes[$index])) {
            Fail-Contract $sourcePath 'machineGrammar.overlap.applicabilityKey must name the predicate axes in declared order'
        }
    }
    Assert-ExactObject $grammar['terminalReplay'] @('row', 'selectionExpansion', 'markerExpansion', 'effects') 'machineGrammar.terminalReplay' $sourcePath
    foreach ($name in $grammar['expansion'].Keys) { Assert-StringValue $grammar['expansion'][$name] "machineGrammar.expansion.$name" $sourcePath }
    foreach ($name in @('rule', 'routing')) { Assert-StringValue $grammar['overlap'][$name] "machineGrammar.overlap.$name" $sourcePath }
    foreach ($name in $grammar['terminalReplay'].Keys) { Assert-StringValue $grammar['terminalReplay'][$name] "machineGrammar.terminalReplay.$name" $sourcePath }

    if ($contract['rows'] -is [string] -or $contract['rows'] -is [System.Collections.IDictionary] -or $contract['rows'] -isnot [System.Collections.IEnumerable]) {
        Fail-Contract $sourcePath 'rows must be a JSON array'
    }
    $rows = @($contract['rows']); if ($rows.Count -eq 0) { Fail-Contract $sourcePath 'rows must not be empty' }
    $rowIds = [Collections.Generic.List[string]]::new()
    $rowIdUniqueness = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $rowsById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $keyOwners = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $rowPath = 'rows[]'
        $allowed = @('id', 'match', 'firstApplicableRetryWrite', 'requiredActions', 'workingFiles', 'branchIdentity', 'durableResult', 'outcome', 'exitClass', 'doctorGuidance', 'resultingMarker', 'nextRows')
        Assert-AllowedObjectProperties $row $allowed @($allowed[0..9]) $rowPath $sourcePath
        Assert-StringValue $row['id'] "$rowPath.id" $sourcePath
        if (-not $rowIdUniqueness.Add($row['id'])) { Fail-Contract $sourcePath "duplicate row ID '$($row['id'])'" }
        if (-not $rowsById.TryAdd($row['id'], $row)) { Fail-Contract $sourcePath "duplicate row ID '$($row['id'])'" }
        $rowIds.Add($row['id'])
        $rowPath = "row $($row['id'])"
        Assert-ExactObject $row['match'] $axes "$rowPath.match" $sourcePath
        $expandedAxes = foreach ($axis in $axes) { ,@(Expand-Predicate $row['match'][$axis] $axis $enums $aggregates $rowPath $sourcePath) }
        $first = $row['firstApplicableRetryWrite']; Assert-StringValue $first "$rowPath.firstApplicableRetryWrite" $sourcePath
        if (-not (Test-OrdinalStringInCollection (Get-DeclaredEnumValues $enums 'firstApplicableRetryWrite' $sourcePath) $first)) { Fail-Contract $sourcePath "$rowPath.firstApplicableRetryWrite is not declared" }
        Assert-StringArray $row['requiredActions'] "$rowPath.requiredActions" $sourcePath $true | Out-Null
        Assert-StringValue $row['workingFiles'] "$rowPath.workingFiles" $sourcePath
        Assert-StringValue $row['branchIdentity'] "$rowPath.branchIdentity" $sourcePath
        Assert-NullableString $row['durableResult'] "$rowPath.durableResult" $sourcePath
        Assert-NullableString $row['outcome'] "$rowPath.outcome" $sourcePath
        Assert-NullableString $row['exitClass'] "$rowPath.exitClass" $sourcePath
        if ($null -ne $row['exitClass'] -and -not (Test-OrdinalStringInCollection (Get-DeclaredEnumValues $enums 'exitClass' $sourcePath) $row['exitClass'])) { Fail-Contract $sourcePath "$rowPath.exitClass is not declared" }
        Assert-NullableBoolean $row['doctorGuidance'] "$rowPath.doctorGuidance" $sourcePath
        if (Test-ExactDictionaryKey $row 'resultingMarker') { Assert-StringValue $row['resultingMarker'] "$rowPath.resultingMarker" $sourcePath }
        if (Test-ExactDictionaryKey $row 'nextRows') {
            $nextRows = @(Assert-StringArray $row['nextRows'] "$rowPath.nextRows" $sourcePath)
            if ($null -ne $row['outcome']) { Fail-Contract $sourcePath "$rowPath routing row must not have an outcome" }
        }
        else {
            if ($null -eq $row['outcome']) { Fail-Contract $sourcePath "$rowPath terminal row must have an outcome" }
        }
        $combinations = [Collections.Generic.List[object]]::new()
        $combinations.Add([string[]]@())
        foreach ($values in $expandedAxes) {
            $next = [Collections.Generic.List[object]]::new()
            foreach ($prefixObject in $combinations) {
                [string[]] $prefix = $prefixObject
                foreach ($value in $values) { $next.Add([string[]]@($prefix + [string]$value)) }
            }
            $combinations = $next
        }
        foreach ($combination in $combinations) {
            $key = [string]::Join([char]31, [string[]]$combination)
            if ($keyOwners.ContainsKey($key)) { Fail-Contract $sourcePath "duplicate applicability key for rows '$($keyOwners[$key])' and '$($row['id'])'" }
            $keyOwners.Add($key, $row['id'])
        }
    }
    if ($rows.Count -ne $rowIds.Count -or $rows.Count -ne $rowsById.Count) { Fail-Contract $sourcePath 'row identity counts are inconsistent' }
    foreach ($row in $rows) {
        if (Test-ExactDictionaryKey $row 'nextRows') {
            foreach ($target in $row['nextRows']) { if (-not $rowsById.ContainsKey($target)) { Fail-Contract $sourcePath "row '$($row['id'])' has dangling nextRows target '$target'" } }
        }
    }
    Assert-VerifiedRootCorrection $rowsById $sourcePath
    Assert-EphemeralBytesChangedTerminalization $rowsById $sourcePath
    $digestBytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))
    return [pscustomobject]@{ Digest = [Convert]::ToHexString($digestBytes).ToLowerInvariant(); RowIds = @($rowIds); ApplicabilityKeys = @($keyOwners.Keys); RowsById = $rowsById }
}

Export-ModuleMember -Function Read-WduLifecycleContract
