Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'WduLifecycleContract.psm1') -Force

$script:PlanStartMarker = '<!-- grace:wdu-lifecycle-projection-plan:start -->'
$script:PlanEndMarker = '<!-- grace:wdu-lifecycle-projection-plan:end -->'
$script:ProjectionStartMarker = '<!-- grace:wdu-lifecycle-projection:start -->'
$script:ProjectionEndMarker = '<!-- grace:wdu-lifecycle-projection:end -->'
$script:PlanSchema = 'grace.wdu.lifecycle-projection-plan/v1'
$script:ProjectionSchema = 'grace.wdu.lifecycle-projection/v1'
$script:RequiredArtifactIds = @(
    'adr-0011',
    'epic-835',
    'issue-842',
    'issue-843',
    'issue-846',
    'issue-869',
    'issue-898',
    'issue-899',
    'issue-900',
    'issue-901',
    'issue-871',
    'issue-872'
)
$script:RequiredNonLifecycleArtifacts = @(
    [pscustomobject]@{
        Artifact = 'issue-897'
        Reason = 'The supersession correction owns packet alignment and publication preparation, not a lifecycle-row delivery; the checker exports lifecycle consumers only.'
    }
)

function Fail-Projection {
    param([string] $Subject, [string] $Reason)
    throw "WDU lifecycle projection '$Subject': $Reason"
}

function Normalize-LineEndings {
    param([string] $Text)
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-OrdinalIndexes {
    param([string] $Text, [string] $Value)
    $indexes = [Collections.Generic.List[int]]::new()
    $offset = 0
    while ($offset -le ($Text.Length - $Value.Length)) {
        $index = $Text.IndexOf($Value, $offset, [StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $indexes.Add($index)
        $offset = $index + $Value.Length
    }
    return @($indexes)
}

function Get-SingleMarkedBlock {
    param([string] $Text, [string] $StartMarker, [string] $EndMarker, [string] $Subject)
    $starts = @(Get-OrdinalIndexes $Text $StartMarker)
    $ends = @(Get-OrdinalIndexes $Text $EndMarker)
    if ($starts.Count -ne 1 -or $ends.Count -ne 1) {
        Fail-Projection $Subject 'requires one exact projection marker pair'
    }
    $contentStart = $starts[0] + $StartMarker.Length
    if ($ends[0] -le $contentStart) { Fail-Projection $Subject 'projection markers are reversed' }
    return [pscustomobject]@{
        Block = $Text.Substring($starts[0], ($ends[0] + $EndMarker.Length) - $starts[0])
        Content = $Text.Substring($contentStart, $ends[0] - $contentStart)
    }
}

function Get-FencedJson {
    param([string] $Content, [string] $Subject)
    $trimmed = $Content.Trim()
    $prefix = '```json' + "`n"
    $suffix = "`n" + '```'
    if (-not $trimmed.StartsWith($prefix, [StringComparison]::Ordinal) -or -not $trimmed.EndsWith($suffix, [StringComparison]::Ordinal)) {
        Fail-Projection $Subject 'marked payload must be one exact fenced JSON object'
    }
    return $trimmed.Substring($prefix.Length, $trimmed.Length - $prefix.Length - $suffix.Length)
}

function Assert-NoDuplicateJsonProperties {
    param([string] $Json, [string] $Subject)
    try { $document = [Text.Json.JsonDocument]::Parse($Json) }
    catch { Fail-Projection $Subject "marked JSON is malformed: $($_.Exception.Message)" }
    try {
        $visit = $null
        $visit = {
            param([Text.Json.JsonElement] $Element, [string] $Location)
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Element.EnumerateObject()) {
                    if (-not $names.Add($property.Name)) {
                        Fail-Projection $Subject "$Location has duplicate case-equivalent property '$($property.Name)'"
                    }
                    & $visit $property.Value "$Location.$($property.Name)"
                }
            }
            elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                $index = 0
                foreach ($item in $Element.EnumerateArray()) {
                    & $visit $item "$Location[$index]"
                    $index++
                }
            }
        }
        & $visit $document.RootElement '$'
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            Fail-Projection $Subject 'marked JSON must be an object'
        }
    }
    finally { $document.Dispose() }
}

function ConvertFrom-ExactJsonObject {
    param([string] $Content, [string] $Subject)
    $json = Get-FencedJson $Content $Subject
    Assert-NoDuplicateJsonProperties $json $Subject
    try { return $json | ConvertFrom-Json -AsHashtable -Depth 32 }
    catch { Fail-Projection $Subject "marked JSON cannot be decoded: $($_.Exception.Message)" }
}

function Test-ExactKey {
    param([System.Collections.IDictionary] $Value, [string] $Name)
    foreach ($key in $Value.Keys) {
        if ([StringComparer]::Ordinal.Equals([string] $key, $Name)) { return $true }
    }
    return $false
}

function Assert-ExactObjectProperties {
    param([object] $Value, [string[]] $Names, [string] $Location, [string] $Subject)
    if ($Value -isnot [System.Collections.IDictionary]) { Fail-Projection $Subject "$Location must be a JSON object" }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Names) { $null = $expected.Add($name) }
    foreach ($key in $Value.Keys) {
        if (-not $expected.Contains([string] $key)) { Fail-Projection $Subject "$Location has unknown property '$key'" }
    }
    foreach ($name in $Names) {
        if (-not (Test-ExactKey $Value $name)) { Fail-Projection $Subject "$Location is missing property '$name'" }
    }
}

function Assert-NonBlankString {
    param([object] $Value, [string] $Location, [string] $Subject)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        Fail-Projection $Subject "$Location must be a nonblank JSON string"
    }
}

function Test-LowercaseSha256 {
    param([string] $Value)
    if ($Value.Length -ne 64) { return $false }
    foreach ($character in $Value.ToCharArray()) {
        if (($character -lt '0' -or $character -gt '9') -and ($character -lt 'a' -or $character -gt 'f')) { return $false }
    }
    return $true
}

function Get-StringArray {
    param([object] $Value, [string] $Location, [string] $Subject)
    if ($Value -is [string] -or $Value -is [System.Collections.IDictionary] -or $Value -isnot [System.Collections.IEnumerable]) {
        Fail-Projection $Subject "$Location must be a nonempty JSON array of strings"
    }
    $values = @($Value)
    if ($values.Count -eq 0) { Fail-Projection $Subject "$Location must be a nonempty JSON array of strings" }
    foreach ($value in $values) { Assert-NonBlankString $value "$Location[]" $Subject }
    return [string[]] $values
}

function Find-Assignment {
    param([object[]] $Assignments, [string] $Artifact, [string] $Subject)
    foreach ($assignment in $Assignments) {
        if ([StringComparer]::Ordinal.Equals($assignment.Artifact, $Artifact)) { return $assignment }
    }
    Fail-Projection $Subject "has unexpected artifact '$Artifact'"
}

function Read-WduLifecycleProjectionPlan {
    param([string] $CanonicalPath, [object] $Compiled)
    $resolved = [IO.Path]::GetFullPath($CanonicalPath)
    $text = Normalize-LineEndings ([IO.File]::ReadAllText($resolved))
    $marked = Get-SingleMarkedBlock $text $script:PlanStartMarker $script:PlanEndMarker $resolved
    $plan = ConvertFrom-ExactJsonObject $marked.Content $resolved
    Assert-ExactObjectProperties $plan @('schema', 'nonLifecycleArtifacts', 'assignments') '$' $resolved
    Assert-NonBlankString $plan['schema'] '$.schema' $resolved
    if (-not [StringComparer]::Ordinal.Equals($plan['schema'], $script:PlanSchema)) {
        Fail-Projection $resolved "$.schema must equal '$script:PlanSchema'"
    }
    if ($plan['nonLifecycleArtifacts'] -is [string] -or $plan['nonLifecycleArtifacts'] -is [System.Collections.IDictionary] -or $plan['nonLifecycleArtifacts'] -isnot [System.Collections.IEnumerable]) {
        Fail-Projection $resolved '$.nonLifecycleArtifacts must be a JSON array'
    }
    $rawNonLifecycleArtifacts = @($plan['nonLifecycleArtifacts'])
    if ($rawNonLifecycleArtifacts.Count -ne $script:RequiredNonLifecycleArtifacts.Count) {
        Fail-Projection $resolved '$.nonLifecycleArtifacts must contain the complete declared non-lifecycle packet'
    }
    for ($nonLifecycleIndex = 0; $nonLifecycleIndex -lt $rawNonLifecycleArtifacts.Count; $nonLifecycleIndex++) {
        $rawNonLifecycle = $rawNonLifecycleArtifacts[$nonLifecycleIndex]
        Assert-ExactObjectProperties $rawNonLifecycle @('artifact', 'reason') '$.nonLifecycleArtifacts[]' $resolved
        Assert-NonBlankString $rawNonLifecycle['artifact'] '$.nonLifecycleArtifacts[].artifact' $resolved
        Assert-NonBlankString $rawNonLifecycle['reason'] '$.nonLifecycleArtifacts[].reason' $resolved
        $requiredNonLifecycle = $script:RequiredNonLifecycleArtifacts[$nonLifecycleIndex]
        if (-not [StringComparer]::Ordinal.Equals($rawNonLifecycle['artifact'], $requiredNonLifecycle.Artifact)) {
            Fail-Projection $resolved "$.nonLifecycleArtifacts must declare excluded artifact '$($requiredNonLifecycle.Artifact)' at ordinal $($nonLifecycleIndex + 1), not '$($rawNonLifecycle['artifact'])'"
        }
        if (-not [StringComparer]::Ordinal.Equals($rawNonLifecycle['reason'], $requiredNonLifecycle.Reason)) {
            Fail-Projection $resolved "$.nonLifecycleArtifacts has a stale reason for excluded artifact '$($requiredNonLifecycle.Artifact)'"
        }
    }
    if ($plan['assignments'] -is [string] -or $plan['assignments'] -is [System.Collections.IDictionary] -or $plan['assignments'] -isnot [System.Collections.IEnumerable]) {
        Fail-Projection $resolved '$.assignments must be a JSON array'
    }
    $rawAssignments = @($plan['assignments'])
    if ($rawAssignments.Count -ne $script:RequiredArtifactIds.Count) { Fail-Projection $resolved "$.assignments must contain the complete $($script:RequiredArtifactIds.Count)-artifact packet" }
    $knownRows = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($rowId in $Compiled.RowIds) { $null = $knownRows.Add($rowId) }
    $coveredRows = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $artifacts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $assignments = [Collections.Generic.List[object]]::new()
    $assignmentIndex = 0
    foreach ($raw in $rawAssignments) {
        Assert-ExactObjectProperties $raw @('artifact', 'canonical', 'canonicalContentDigest', 'rowIds', 'proof') '$.assignments[]' $resolved
        foreach ($name in @('artifact', 'canonical', 'canonicalContentDigest', 'proof')) {
            Assert-NonBlankString $raw[$name] "$.assignments[].$name" $resolved
        }
        if (-not $artifacts.Add($raw['artifact'])) { Fail-Projection $resolved "$.assignments contains duplicate artifact '$($raw['artifact'])'" }
        $requiredArtifact = $script:RequiredArtifactIds[$assignmentIndex]
        if (-not [StringComparer]::Ordinal.Equals($raw['artifact'], $requiredArtifact)) {
            Fail-Projection $resolved "$.assignments must declare required artifact '$requiredArtifact' at ordinal $($assignmentIndex + 1), not '$($raw['artifact'])'"
        }
        if (-not (Test-LowercaseSha256 $raw['canonicalContentDigest'])) {
            Fail-Projection $resolved '$.assignments[].canonicalContentDigest must be a lowercase SHA-256 digest'
        }
        if (-not [StringComparer]::Ordinal.Equals($raw['canonicalContentDigest'], $Compiled.Digest)) {
            Fail-Projection $resolved "$.assignments[].canonicalContentDigest is stale for '$($raw['artifact'])'"
        }
        $rowIds = @(Get-StringArray $raw['rowIds'] '$.assignments[].rowIds' $resolved)
        $assignedRows = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($rowId in $rowIds) {
            if (-not $knownRows.Contains($rowId)) { Fail-Projection $resolved "$.assignments[] has unknown row ID '$rowId'" }
            if (-not $assignedRows.Add($rowId)) { Fail-Projection $resolved "$.assignments[] has duplicate row ID '$rowId'" }
            $null = $coveredRows.Add($rowId)
        }
        $assignments.Add([pscustomobject]@{
                Artifact = [string] $raw['artifact']
                Canonical = [string] $raw['canonical']
                CanonicalContentDigest = [string] $raw['canonicalContentDigest']
                RowIds = [string[]] $rowIds
                Proof = [string] $raw['proof']
            })
        $assignmentIndex++
    }
    foreach ($rowId in $Compiled.RowIds) {
        if (-not $coveredRows.Contains($rowId)) { Fail-Projection $resolved "$.assignments does not cover canonical row ID '$rowId'" }
    }
    return @($assignments)
}

function New-ProjectionText {
    param([object] $Assignment)
    $projection = [ordered]@{
        schema = $script:ProjectionSchema
        artifact = $Assignment.Artifact
        canonical = $Assignment.Canonical
        canonicalContentDigest = $Assignment.CanonicalContentDigest
        rowIds = @($Assignment.RowIds)
        proof = $Assignment.Proof
    }
    $json = $projection | ConvertTo-Json -Depth 4
    return $script:ProjectionStartMarker + "`n" + '```json' + "`n$json`n" + '```' + "`n" + $script:ProjectionEndMarker
}

function Get-ProjectionArtifact {
    param([string] $Content, [string] $Subject)
    $projection = ConvertFrom-ExactJsonObject $Content $Subject
    if ($projection -isnot [System.Collections.IDictionary] -or -not (Test-ExactKey $projection 'artifact')) {
        Fail-Projection $Subject "projection is missing property 'artifact'"
    }
    Assert-NonBlankString $projection['artifact'] '$.artifact' $Subject
    return [string] $projection['artifact']
}

function New-WduLifecycleProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $CanonicalPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Artifact
    )
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $assignments = @(Read-WduLifecycleProjectionPlan -CanonicalPath $CanonicalPath -Compiled $compiled)
    $assignment = Find-Assignment $assignments $Artifact $CanonicalPath
    return New-ProjectionText $assignment
}

function Test-WduLifecycleProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $CanonicalPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]] $ArtifactPath
    )
    $compiled = Read-WduLifecycleContract -Path $CanonicalPath
    $assignments = @(Read-WduLifecycleProjectionPlan -CanonicalPath $CanonicalPath -Compiled $compiled)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pathsByArtifact = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($path in $ArtifactPath) {
        $resolved = [IO.Path]::GetFullPath($path)
        $text = Normalize-LineEndings ([IO.File]::ReadAllText($resolved))
        $marked = Get-SingleMarkedBlock $text $script:ProjectionStartMarker $script:ProjectionEndMarker $resolved
        $artifact = Get-ProjectionArtifact $marked.Content $resolved
        if (-not $seen.Add($artifact)) { Fail-Projection $resolved "packet contains duplicate artifact '$artifact'" }
        $assignment = Find-Assignment $assignments $artifact $resolved
        $expected = Normalize-LineEndings (New-ProjectionText $assignment)
        if (-not [StringComparer]::Ordinal.Equals($marked.Block, $expected)) {
            Fail-Projection $resolved "artifact '$artifact' does not equal its exact generated projection"
        }
        $pathsByArtifact.Add($artifact, $resolved)
    }
    foreach ($assignment in $assignments) {
        if (-not $seen.Contains($assignment.Artifact)) {
            Fail-Projection $CanonicalPath "packet is missing required artifact '$($assignment.Artifact)'"
        }
    }
    foreach ($assignment in $assignments) {
        [pscustomobject]@{ Artifact = $assignment.Artifact; Path = $pathsByArtifact[$assignment.Artifact]; Result = 'ExactMatch' }
    }
}

Export-ModuleMember -Function New-WduLifecycleProjection, Test-WduLifecycleProjection
