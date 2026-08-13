[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$modulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleRawProjection.psm1'
$contractModulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleContract.psm1'
$canonicalPath = Join-Path $repositoryRoot 'docs/Working Directory Update.md'

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
    try { & $Body; $script:Passed++; Write-Host "PASS $Name" }
    catch { $script:Failed++; Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red }
}

function Get-Utf8 {
    param([string] $Json)
    return [ReadOnlyMemory[byte]]::new([Text.UTF8Encoding]::new($false, $true).GetBytes($Json))
}

function Get-Json {
    param([object] $Projection)
    return [Text.UTF8Encoding]::new($false, $true).GetString($Projection.Utf8Json.ToArray())
}

function Replace-Once {
    param([string] $Text, [string] $Old, [string] $New)
    $index = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Test anchor not found: $Old" }
    return $Text.Remove($index, $Old.Length).Insert($index, $New)
}

function Replace-ArrayValue {
    param([string] $Json, [string] $Name, [string] $Replacement)
    $prefix = '"' + $Name + '":['
    $start = $Json.IndexOf($prefix, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Test array anchor not found: $Name" }
    $open = $start + $prefix.Length - 1
    $depth = 0
    $inString = $false
    $escaped = $false
    for ($index = $open; $index -lt $Json.Length; $index++) {
        $character = $Json[$index]
        if ($inString) {
            if ($escaped) { $escaped = $false; continue }
            if ($character -eq '\') { $escaped = $true; continue }
            if ($character -eq '"') { $inString = $false }
            continue
        }
        if ($character -eq '"') { $inString = $true; continue }
        if ($character -eq '[') { $depth++; continue }
        if ($character -eq ']') {
            $depth--
            if ($depth -eq 0) { return $Json.Remove($open, $index - $open + 1).Insert($open, $Replacement) }
        }
    }
    throw "Test array has no closing bracket: $Name"
}

function Invoke-Mutation {
    param([object] $Projection, [scriptblock] $Mutation, [string] $Expected)
    $json = Get-Json $Projection
    $mutated = & $Mutation $json
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 $mutated) } $Expected
}

function Assert-IndependentRawPayload {
    param([object] $Projection, [object] $Compiled)
    $document = [Text.Json.JsonDocument]::Parse($Projection.Utf8Json)
    try {
        $root = $document.RootElement
        Assert-True ($root.ValueKind -eq [Text.Json.JsonValueKind]::Object) "$($Projection.Artifact) root is an object"
        $properties = @($root.EnumerateObject())
        $expectedNames = @('schema', 'artifact', 'canonical', 'canonicalContentDigest', 'assignmentDigest', 'rowCount', 'applicabilityKeyCount', 'requirementCount', 'artifactCount', 'requirements', 'artifactIds', 'assignment')
        Assert-True ($properties.Count -eq $expectedNames.Count) "$($Projection.Artifact) has twelve root properties"
        for ($index = 0; $index -lt $expectedNames.Count; $index++) {
            Assert-True ([StringComparer]::Ordinal.Equals($properties[$index].Name, $expectedNames[$index])) "$($Projection.Artifact) root property $index is ordered"
        }
        Assert-True ([StringComparer]::Ordinal.Equals($properties[0].Value.GetString(), 'grace.wdu.lifecycle-projection/v2')) "$($Projection.Artifact) schema is fixed"
        Assert-True ([StringComparer]::Ordinal.Equals($properties[1].Value.GetString(), $Projection.Artifact)) "$($Projection.Artifact) identity is exact"
        Assert-True ([StringComparer]::Ordinal.Equals($properties[2].Value.GetString(), 'docs/Working Directory Update.md#normative-branch-lifecycle-table')) "$($Projection.Artifact) canonical anchor is fixed"
        Assert-True ([StringComparer]::Ordinal.Equals($properties[3].Value.GetString(), $Compiled.Digest)) "$($Projection.Artifact) digest comes from compiler"
        Assert-True ([StringComparer]::Ordinal.Equals($properties[4].Value.GetString(), $Compiled.AssignmentDigest)) "$($Projection.Artifact) assignment digest comes from compiler"
        $expectedCounts = @($Compiled.Counts.rowCount, $Compiled.Counts.applicabilityKeyCount, $Compiled.Counts.requirementCount, $Compiled.Counts.artifactCount)
        foreach ($index in 5..8) {
            Assert-True ($properties[$index].Value.ValueKind -eq [Text.Json.JsonValueKind]::Number) "$($Projection.Artifact) count $index is numeric"
            Assert-True ($properties[$index].Value.GetRawText() -ceq [Convert]::ToString($expectedCounts[$index - 5], [Globalization.CultureInfo]::InvariantCulture)) "$($Projection.Artifact) count $index is exact"
        }
        $requirements = @($properties[9].Value.EnumerateArray())
        Assert-True ($requirements.Count -eq $Compiled.Requirements.Count) "$($Projection.Artifact) has all requirement pairs"
        for ($index = 0; $index -lt $requirements.Count; $index++) {
            $pair = @($requirements[$index].EnumerateObject())
            Assert-True ([StringComparer]::Ordinal.Equals($pair[0].Name, 'id')) "$($Projection.Artifact) requirement $index ID name"
            Assert-True ([StringComparer]::Ordinal.Equals($pair[1].Name, 'owner')) "$($Projection.Artifact) requirement $index owner name"
            Assert-True ([StringComparer]::Ordinal.Equals($pair[0].Value.GetString(), $Compiled.Requirements[$index].Id)) "$($Projection.Artifact) requirement $index ID"
            Assert-True ([StringComparer]::Ordinal.Equals($pair[1].Value.GetString(), $Compiled.Requirements[$index].Owner)) "$($Projection.Artifact) requirement $index owner"
        }
        $artifactIds = @($properties[10].Value.EnumerateArray())
        Assert-True ($artifactIds.Count -eq $Compiled.Artifacts.Count) "$($Projection.Artifact) has all artifact IDs"
        for ($index = 0; $index -lt $artifactIds.Count; $index++) {
            Assert-True ([StringComparer]::Ordinal.Equals($artifactIds[$index].GetString(), $Compiled.Artifacts[$index].Id)) "$($Projection.Artifact) artifact ID $index"
        }
        $compilerArtifact = @($Compiled.Artifacts | Where-Object { [StringComparer]::Ordinal.Equals($_.Id, $Projection.Artifact) })
        $assignmentProperties = @($properties[11].Value.EnumerateObject())
        Assert-True ($properties[11].Value.ValueKind -eq [Text.Json.JsonValueKind]::Object) "$($Projection.Artifact) assignment is an object"
        Assert-True ($assignmentProperties.Count -eq 1) "$($Projection.Artifact) assignment has one property"
        Assert-True ([StringComparer]::Ordinal.Equals($assignmentProperties[0].Name, 'rowIds')) "$($Projection.Artifact) assignment property is rowIds"
        Assert-True ($assignmentProperties[0].Value.ValueKind -eq [Text.Json.JsonValueKind]::Array) "$($Projection.Artifact) row IDs are an array"
        $rowIds = @($assignmentProperties[0].Value.EnumerateArray())
        Assert-True ($compilerArtifact.Count -eq 1) "$($Projection.Artifact) compiler artifact is unique"
        Assert-True ($rowIds.Count -eq $compilerArtifact[0].RowIds.Count) "$($Projection.Artifact) assignment count"
        for ($index = 0; $index -lt $rowIds.Count; $index++) {
            Assert-True ([StringComparer]::Ordinal.Equals($rowIds[$index].GetString(), $compilerArtifact[0].RowIds[$index])) "$($Projection.Artifact) row ID $index"
        }
    }
    finally { $document.Dispose() }
}

Import-Module $modulePath -Force
Import-Module $contractModulePath -Force
$script:Compiled = Read-WduLifecycleContract -Path $canonicalPath
$script:Projections = @($script:Compiled.Artifacts | ForEach-Object { New-WduLifecycleRawProjection -Compiled $script:Compiled -Artifact $_.Id })
$script:Passed = 0
$script:Failed = 0

Invoke-Case 'exports only the raw compiler and validator' {
    $exports = @(Get-Command -Module WduLifecycleRawProjection | Select-Object -ExpandProperty Name)
    Assert-True ($exports.Count -eq 2) 'module has two public commands'
    Assert-True ($exports -contains 'New-WduLifecycleRawProjection') 'compiler is exported'
    Assert-True ($exports -contains 'Test-WduLifecycleRawProjection') 'validator is exported'
}

Invoke-Case 'compiles all fifteen artifacts deterministically and validates raw tokens' {
    Assert-True ($script:Compiled.Counts.rowCount -eq 70) 'compiler row count is 70'
    Assert-True ($script:Compiled.Counts.applicabilityKeyCount -eq 260) 'compiler applicability count is 260'
    Assert-True ($script:Compiled.Requirements.Count -eq 19) 'compiler requirement count is 19'
    Assert-True ($script:Compiled.Artifacts.Count -eq 15) 'compiler artifact count is 15'
    Assert-True ([StringComparer]::Ordinal.Equals($script:Compiled.Digest, 'ae3a77e28886485b49361d8836f040691e9f99228919cef87fac19b42e989d73')) 'compiler digest is exact'
    Assert-True ([StringComparer]::Ordinal.Equals($script:Compiled.AssignmentDigest, '20e329bd3aa4459a01f4ed3c6ec12cf365c86df3538b0323400639b90eeee877')) 'assignment digest is exact'
    foreach ($projection in $script:Projections) {
        $second = New-WduLifecycleRawProjection -Compiled $script:Compiled -Artifact $projection.Artifact
        Assert-True ([Convert]::ToHexString($projection.Utf8Json.ToArray()) -ceq [Convert]::ToHexString($second.Utf8Json.ToArray())) "$($projection.Artifact) bytes are deterministic"
        $validated = Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json $projection.Utf8Json
        Assert-True ([StringComparer]::Ordinal.Equals($validated.Artifact, $projection.Artifact)) "$($projection.Artifact) validates itself"
        Assert-IndependentRawPayload $projection $script:Compiled
    }
}

$baseline = $script:Projections[0]
Invoke-Case 'rejects root array and scalar raw values' {
    $json = Get-Json $baseline
    foreach ($candidate in @("[$json]", 'null', '"projection"', '70')) {
        Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 $candidate) } 'JSON object'
    }
}

Invoke-Case 'rejects duplicate and case-equivalent root and nested properties' {
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"schema":"grace.wdu.lifecycle-projection/v2",' '"schema":"grace.wdu.lifecycle-projection/v2","schema":"grace.wdu.lifecycle-projection/v2",' } 'duplicate case-equivalent'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"schema":"grace.wdu.lifecycle-projection/v2",' '"schema":"grace.wdu.lifecycle-projection/v2","Schema":"grace.wdu.lifecycle-projection/v2",' } 'duplicate case-equivalent'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"id":"REQ-001","owner":"#923"' '"id":"REQ-001","Id":"REQ-001","owner":"#923"' } 'duplicate case-equivalent'
}

Invoke-Case 'rejects missing extra reordered and incorrectly cased properties' {
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"canonical":"docs/Working Directory Update.md#normative-branch-lifecycle-table",' '' } 'ordered properties'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"schema":"grace.wdu.lifecycle-projection/v2",' '"schema":"grace.wdu.lifecycle-projection/v2","extra":true,' } 'ordered properties'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"schema":"grace.wdu.lifecycle-projection/v2","artifact"' '"artifact"' } 'ordered properties'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"schema":"grace.wdu.lifecycle-projection/v2"' '"Schema":"grace.wdu.lifecycle-projection/v2"' } 'property at ordinal'
}

Invoke-Case 'rejects count coercion token kinds and values' {
    foreach ($replacement in @('"70"', '70.0', '7e1', 'null', '71')) {
        Invoke-Mutation $baseline { param($json) Replace-Once $json '"rowCount":70' ('"rowCount":' + $replacement) } 'rowCount'
    }
}

Invoke-Case 'rejects scalar object and null where every required array belongs' {
    foreach ($name in @('requirements', 'artifactIds')) {
        foreach ($replacement in @('"joined"', '{}', 'null')) {
            Invoke-Mutation $baseline { param($json) Replace-ArrayValue $json $name $replacement } $name
        }
    }
    foreach ($replacement in @('"joined"', '{}', 'null')) {
        Invoke-Mutation $baseline { param($json) Replace-ArrayValue $json 'rowIds' $replacement } 'rowIds'
    }
}

Invoke-Case 'rejects requirement and artifact vector drift' {
    Invoke-Mutation $baseline { param($json) Replace-Once $json '{"id":"REQ-001","owner":"#923"},' '' } 'requirements'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '{"id":"REQ-001","owner":"#923"},{"id":"REQ-002","owner":"#869"}' '{"id":"REQ-002","owner":"#869"},{"id":"REQ-001","owner":"#923"}' } 'requirements[0].id'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '{"id":"REQ-001","owner":"#923"},' '{"id":"REQ-001","owner":"#923"},{"id":"REQ-001","owner":"#923"},' } 'requirements'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"id":"REQ-001","owner":"#923"' '"id":"REQ-001","owner":"#999"' } 'requirements[0].owner'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"id":"REQ-001","owner":"#923"' '"id":"REQ-001","owner":"#923","extra":true' } 'ordered properties'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"REQ-001"' '"req-001"' } 'requirements[0].id'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"adr-0011","epic-835"' '"epic-835","adr-0011"' } 'artifactIds[0]'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"artifactIds":["adr-0011",' '"artifactIds":[' } 'artifactIds'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"artifactIds":["adr-0011",' '"artifactIds":["adr-0011","adr-0011",' } 'artifactIds'
    Invoke-Mutation $baseline { param($json) Replace-Once $json '"artifactIds":["adr-0011"' '"artifactIds":["ADR-0011"' } 'artifactIds[0]'
}

Invoke-Case 'rejects assignment row drift for every compiler artifact' {
    foreach ($projection in $script:Projections) {
        $artifact = @($script:Compiled.Artifacts | Where-Object { [StringComparer]::Ordinal.Equals($_.Id, $projection.Artifact) })[0]
        $first = $artifact.RowIds[0]
        $second = $artifact.RowIds[1]
        Invoke-Mutation $projection { param($json) Replace-Once $json ('"' + $first + '",') '' } 'assignment.rowIds'
        Invoke-Mutation $projection { param($json) Replace-Once $json ('"' + $first + '","' + $second + '"') ('"' + $second + '","' + $first + '"') } 'assignment.rowIds[0]'
        Invoke-Mutation $projection { param($json) Replace-Once $json ('"' + $first + '",') ('"' + $first + '","' + $first + '",') } 'assignment.rowIds'
        Invoke-Mutation $projection { param($json) Replace-Once $json ('"' + $first + '"') ('"' + $first.ToLowerInvariant() + '"') } 'assignment.rowIds[0]'
    }
}

Invoke-Case 'rejects trailing tokens comments BOM and malformed UTF-8' {
    $json = Get-Json $baseline
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 ($json + '{}')) } 'one valid UTF-8 JSON value'
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 ($json + '/*comment*/')) } 'one valid UTF-8 JSON value'
    $bom = [byte[]](0xEF, 0xBB, 0xBF) + $baseline.Utf8Json.ToArray()
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json ([ReadOnlyMemory[byte]]::new($bom)) } 'UTF-8 BOM'
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json ([ReadOnlyMemory[byte]]::new([byte[]](0xFF))) } 'one valid UTF-8 JSON value'
}

Invoke-Case 'rejects raw shapes PowerShell can coerce into apparent equality' {
    $json = Get-Json $baseline
    $array = "[$json]" | ConvertFrom-Json
    Assert-True ([StringComparer]::Ordinal.Equals($array.schema, 'grace.wdu.lifecycle-projection/v2')) 'PowerShell unwraps one element array property access'
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 "[$json]") } 'JSON object'
    $stringCountJson = Replace-Once $json '"rowCount":70' '"rowCount":"70"'
    $stringCount = $stringCountJson | ConvertFrom-Json
    Assert-True ($stringCount.rowCount -eq 70) 'PowerShell coerces the string count in comparison'
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 $stringCountJson) } 'rowCount'
    $scalarArrayJson = Replace-ArrayValue $json 'artifactIds' '"adr-0011,epic-835"'
    $scalarArray = $scalarArrayJson | ConvertFrom-Json
    Assert-True ($scalarArray.artifactIds -is [string]) 'PowerShell exposes a scalar where the JSON contract requires an array'
    Assert-Fails { Test-WduLifecycleRawProjection -Compiled $script:Compiled -Utf8Json (Get-Utf8 $scalarArrayJson) } 'artifactIds'
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
