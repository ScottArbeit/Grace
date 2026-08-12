Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:StartMarker = '<!-- grace:wdu-lifecycle-contract:start -->'
$script:EndMarker = '<!-- grace:wdu-lifecycle-contract:end -->'

function Fail-Contract {
    param([string] $Path, [string] $Reason)
    throw "WDU lifecycle contract '$Path': $Reason"
}

function Test-ExactDictionaryKey {
    param([System.Collections.IDictionary] $Value, [string] $Name)
    foreach ($key in $Value.Keys) {
        if ([string] $key -ceq $Name) { return $true }
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
            if ([string] $name -ieq 'kind') { Fail-Contract $SourcePath "$RowPath.$Axis has unknown property '$name'" }
        }
        Fail-Contract $SourcePath "$RowPath.$Axis must be a predicate object with kind"
    }
    $values = Get-DeclaredEnumValues $ConcreteEnums $Axis $SourcePath
    $kind = $Predicate['kind']; Assert-StringValue $kind "$RowPath.$Axis.kind" $SourcePath
    switch ($kind) {
        'one' {
            Assert-ExactObject $Predicate @('kind', 'value') "$RowPath.$Axis" $SourcePath
            Assert-StringValue $Predicate['value'] "$RowPath.$Axis.value" $SourcePath
            $expanded = @($Predicate['value'])
        }
        'set' {
            Assert-ExactObject $Predicate @('kind', 'values') "$RowPath.$Axis" $SourcePath
            $expanded = @(Assert-StringArray $Predicate['values'] "$RowPath.$Axis.values" $SourcePath)
            Assert-UniqueStrings $expanded "$RowPath.$Axis.values" $SourcePath
        }
        'aggregate' {
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
        default { Fail-Contract $SourcePath "$RowPath.$Axis has unknown predicate kind '$kind'" }
    }
    foreach ($value in $expanded) {
        if ($values -cnotcontains $value) { Fail-Contract $SourcePath "$RowPath.$Axis value '$value' is not declared" }
    }
    return $expanded
}

function Read-WduLifecycleContract {
    param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Path)

    $sourcePath = [IO.Path]::GetFullPath($Path)
    $text = [IO.File]::ReadAllText($sourcePath)
    $lines = @($text -split "`r`n|`n|`r")
    $starts = @($lines | Where-Object { $_ -ceq $script:StartMarker })
    $ends = @($lines | Where-Object { $_ -ceq $script:EndMarker })
    if ($starts.Count -ne 1 -or $ends.Count -ne 1) { Fail-Contract $sourcePath 'requires one exact lifecycle marker pair' }
    $start = [Array]::IndexOf($lines, $script:StartMarker)
    $end = [Array]::IndexOf($lines, $script:EndMarker)
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
        $rawRows = @($document.RootElement.EnumerateObject() | Where-Object { $_.Name -ceq 'rows' })
        if ($rawRows.Count -eq 1 -and $rawRows[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
            Fail-Contract $sourcePath 'rows must be a JSON array'
        }
    }
    finally { $document.Dispose() }
    try { $contract = $json | ConvertFrom-Json -AsHashtable -Depth 100 }
    catch { Fail-Contract $sourcePath "marked JSON cannot be decoded: $($_.Exception.Message)" }

    Assert-ExactObject $contract @('schema', 'boundaries', 'retryAdmission', 'doctorCommand', 'order', 'machineGrammar', 'rows') '$' $sourcePath
    if ($contract['schema'] -cne 'grace.wdu.branch-lifecycle/v1') { Fail-Contract $sourcePath 'schema must be grace.wdu.branch-lifecycle/v1' }
    Assert-ExactObject $contract['boundaries'] @('firstWorkingTreeMutation', 'sqliteLocalCompletion', 'firstApplicableRetryWrite') 'boundaries' $sourcePath
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
        $shapeNames = if ($kind -eq 'one') { @('kind', 'value') } elseif ($kind -eq 'set') { @('kind', 'values') } else { @('kind', 'name') }
        $shape = $grammar['encoding'][$kind]['jsonShape']
        Assert-ExactObject $shape $shapeNames "machineGrammar.encoding.$kind.jsonShape" $sourcePath
        Assert-StringValue $shape['kind'] "machineGrammar.encoding.$kind.jsonShape.kind" $sourcePath
        if ($shape['kind'] -cne $kind) { Fail-Contract $sourcePath "machineGrammar.encoding.$kind must declare kind '$kind'" }
        if ($kind -eq 'set') { Assert-StringArray $shape['values'] "machineGrammar.encoding.$kind.jsonShape.values" $sourcePath | Out-Null }
        else { Assert-StringValue $shape[$shapeNames[1]] "machineGrammar.encoding.$kind.jsonShape.$($shapeNames[1])" $sourcePath }
        Assert-StringValue $grammar['encoding'][$kind]['meaning'] "machineGrammar.encoding.$kind.meaning" $sourcePath
    }
    $enums = $grammar['concreteEnums']; if ($enums -isnot [System.Collections.IDictionary]) { Fail-Contract $sourcePath 'machineGrammar.concreteEnums must be an object' }
    foreach ($name in $enums.Keys) { $members = @(Assert-StringArray $enums[$name] "machineGrammar.concreteEnums.$name" $sourcePath); Assert-UniqueStrings $members "machineGrammar.concreteEnums.$name" $sourcePath }
    foreach ($axis in $axes) { Get-DeclaredEnumValues $enums $axis $sourcePath | Out-Null }
    $aggregates = $grammar['aggregates']; if ($aggregates -isnot [System.Collections.IDictionary]) { Fail-Contract $sourcePath 'machineGrammar.aggregates must be an object' }
    foreach ($axis in $aggregates.Keys) {
        if ($axes -cnotcontains $axis -or $aggregates[$axis] -isnot [System.Collections.IDictionary]) { Fail-Contract $sourcePath "machineGrammar.aggregates.$axis is not an axis aggregate object" }
        foreach ($name in $aggregates[$axis].Keys) {
            $members = @(Assert-StringArray $aggregates[$axis][$name] "machineGrammar.aggregates.$axis.$name" $sourcePath)
            Assert-UniqueStrings $members "machineGrammar.aggregates.$axis.$name" $sourcePath
            $declared = Get-DeclaredEnumValues $enums $axis $sourcePath
            foreach ($member in $members) { if ($declared -cnotcontains $member) { Fail-Contract $sourcePath "machineGrammar.aggregates.$axis.$name has undeclared member '$member'" } }
        }
    }
    Assert-ExactObject $grammar['expansion'] @('rule', 'setMembers', 'aggregateMembers', 'example') 'machineGrammar.expansion' $sourcePath
    Assert-ExactObject $grammar['overlap'] @('applicabilityKey', 'rule', 'routing') 'machineGrammar.overlap' $sourcePath
    $keyAxes = @(Assert-StringArray $grammar['overlap']['applicabilityKey'] 'machineGrammar.overlap.applicabilityKey' $sourcePath)
    if (@(Compare-Object $axes $keyAxes -SyncWindow 0).Count -ne 0) { Fail-Contract $sourcePath 'machineGrammar.overlap.applicabilityKey must name the predicate axes' }
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
        for ($i = 0; $i -lt $expandedAxes[0].Count; $i++) { }
        $first = $row['firstApplicableRetryWrite']; Assert-StringValue $first "$rowPath.firstApplicableRetryWrite" $sourcePath
        if ((Get-DeclaredEnumValues $enums 'firstApplicableRetryWrite' $sourcePath) -cnotcontains $first) { Fail-Contract $sourcePath "$rowPath.firstApplicableRetryWrite is not declared" }
        Assert-StringArray $row['requiredActions'] "$rowPath.requiredActions" $sourcePath $true | Out-Null
        Assert-StringValue $row['workingFiles'] "$rowPath.workingFiles" $sourcePath
        Assert-StringValue $row['branchIdentity'] "$rowPath.branchIdentity" $sourcePath
        Assert-NullableString $row['durableResult'] "$rowPath.durableResult" $sourcePath
        Assert-NullableString $row['outcome'] "$rowPath.outcome" $sourcePath
        Assert-NullableString $row['exitClass'] "$rowPath.exitClass" $sourcePath
        if ($null -ne $row['exitClass'] -and (Get-DeclaredEnumValues $enums 'exitClass' $sourcePath) -cnotcontains $row['exitClass']) { Fail-Contract $sourcePath "$rowPath.exitClass is not declared" }
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
    $digestBytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))
    return [pscustomobject]@{ Digest = [Convert]::ToHexString($digestBytes).ToLowerInvariant(); RowIds = @($rowIds); ApplicabilityKeys = @($keyOwners.Keys); RowsById = $rowsById }
}

Export-ModuleMember -Function Read-WduLifecycleContract
