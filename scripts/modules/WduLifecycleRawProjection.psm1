Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'WduLifecycleContract.psm1') -Force

$script:ProjectionSchema = 'grace.wdu.lifecycle-projection/v2'
$script:CanonicalAnchor = 'docs/Working Directory Update.md#normative-branch-lifecycle-table'
$script:RootProperties = @(
    'schema', 'artifact', 'canonical', 'canonicalContentDigest', 'assignmentDigest', 'rowCount',
    'applicabilityKeyCount', 'requirementCount', 'artifactCount', 'requirements', 'artifactIds', 'assignment'
)

function Fail-RawProjection {
    param([string] $Location, [string] $Reason)
    throw "WDU raw lifecycle projection '$Location': $Reason"
}

function Test-OrdinalEqual {
    param([string] $Left, [string] $Right)
    return [StringComparer]::Ordinal.Equals($Left, $Right)
}

function Get-CompilerArtifact {
    param([object] $Compiled, [string] $ArtifactId, [string] $Location)
    foreach ($artifact in $Compiled.Artifacts) {
        if (Test-OrdinalEqual $artifact.Id $ArtifactId) { return $artifact }
    }
    Fail-RawProjection $Location "artifact '$ArtifactId' is not declared by the lifecycle compiler"
}

function Assert-NoDuplicateProperties {
    param([Text.Json.JsonElement] $Element, [string] $Location)
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                Fail-RawProjection $Location "has duplicate case-equivalent property '$($property.Name)'"
            }
            Assert-NoDuplicateProperties $property.Value "$Location.$($property.Name)"
        }
        return
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateProperties $item "$Location[$index]"
            $index++
        }
    }
}

function Assert-ExactProperties {
    param([Text.Json.JsonElement] $Element, [string[]] $Expected, [string] $Location)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        Fail-RawProjection $Location 'must be a JSON object'
    }
    $properties = @($Element.EnumerateObject())
    if ($properties.Count -ne $Expected.Count) {
        Fail-RawProjection $Location "must contain exactly $($Expected.Count) ordered properties"
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not (Test-OrdinalEqual $properties[$index].Name $Expected[$index])) {
            Fail-RawProjection $Location "property at ordinal $index must be '$($Expected[$index])'"
        }
    }
    return $properties
}

function Assert-JsonString {
    param([Text.Json.JsonElement] $Element, [string] $Location)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::String) {
        Fail-RawProjection $Location 'must be a JSON string'
    }
    return $Element.GetString()
}

function Assert-ExactString {
    param([Text.Json.JsonElement] $Element, [string] $Expected, [string] $Location)
    $actual = Assert-JsonString $Element $Location
    if (-not (Test-OrdinalEqual $actual $Expected)) {
        Fail-RawProjection $Location "must equal '$Expected'"
    }
}

function Assert-ExactCount {
    param([Text.Json.JsonElement] $Element, [int] $Expected, [string] $Location)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Number) {
        Fail-RawProjection $Location 'must be a JSON number'
    }
    if (-not (Test-OrdinalEqual $Element.GetRawText() ([Convert]::ToString($Expected, [Globalization.CultureInfo]::InvariantCulture)))) {
        Fail-RawProjection $Location "must be the exact integer token $Expected"
    }
}

function Assert-StringVector {
    param([Text.Json.JsonElement] $Element, [string[]] $Expected, [string] $Location)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
        Fail-RawProjection $Location 'must be a JSON array'
    }
    $values = @($Element.EnumerateArray())
    if ($values.Count -ne $Expected.Count) {
        Fail-RawProjection $Location "must contain exactly $($Expected.Count) values"
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-ExactString $values[$index] $Expected[$index] "$Location[$index]"
    }
}

function Assert-Requirements {
    param([Text.Json.JsonElement] $Element, [object[]] $Expected, [string] $Location)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
        Fail-RawProjection $Location 'must be a JSON array'
    }
    $requirements = @($Element.EnumerateArray())
    if ($requirements.Count -ne $Expected.Count) {
        Fail-RawProjection $Location "must contain exactly $($Expected.Count) values"
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        $properties = @(Assert-ExactProperties $requirements[$index] @('id', 'owner') "$Location[$index]")
        Assert-ExactString $properties[0].Value $Expected[$index].Id "$Location[$index].id"
        Assert-ExactString $properties[1].Value $Expected[$index].Owner "$Location[$index].owner"
    }
}

function New-WduLifecycleRawProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNull()][object] $Compiled,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Artifact
    )

    $declaredArtifact = Get-CompilerArtifact $Compiled $Artifact '$.artifact'
    $stream = [IO.MemoryStream]::new()
    $options = [Text.Json.JsonWriterOptions]::new()
    $options.Indented = $false
    $writer = [Text.Json.Utf8JsonWriter]::new($stream, $options)
    try {
        $writer.WriteStartObject()
        $writer.WriteString('schema', $script:ProjectionSchema)
        $writer.WriteString('artifact', $declaredArtifact.Id)
        $writer.WriteString('canonical', $script:CanonicalAnchor)
        $writer.WriteString('canonicalContentDigest', $compiled.Digest)
        $writer.WriteString('assignmentDigest', $compiled.AssignmentDigest)
        $writer.WriteNumber('rowCount', $compiled.Counts.rowCount)
        $writer.WriteNumber('applicabilityKeyCount', $compiled.Counts.applicabilityKeyCount)
        $writer.WriteNumber('requirementCount', $compiled.Counts.requirementCount)
        $writer.WriteNumber('artifactCount', $compiled.Counts.artifactCount)
        $writer.WritePropertyName('requirements')
        $writer.WriteStartArray()
        foreach ($requirement in $compiled.Requirements) {
            $writer.WriteStartObject()
            $writer.WriteString('id', $requirement.Id)
            $writer.WriteString('owner', $requirement.Owner)
            $writer.WriteEndObject()
        }
        $writer.WriteEndArray()
        $writer.WritePropertyName('artifactIds')
        $writer.WriteStartArray()
        foreach ($compilerArtifact in $compiled.Artifacts) { $writer.WriteStringValue($compilerArtifact.Id) }
        $writer.WriteEndArray()
        $writer.WritePropertyName('assignment')
        $writer.WriteStartObject()
        $writer.WritePropertyName('rowIds')
        $writer.WriteStartArray()
        foreach ($rowId in $declaredArtifact.RowIds) { $writer.WriteStringValue($rowId) }
        $writer.WriteEndArray()
        $writer.WriteEndObject()
        $writer.WriteEndObject()
        $writer.Flush()
        $bytes = $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
    $result = [pscustomobject]@{
        Artifact = $declaredArtifact.Id
        Utf8Json = [ReadOnlyMemory[byte]]::new($bytes)
    }
    Test-WduLifecycleRawProjection -Compiled $Compiled -Utf8Json $result.Utf8Json | Out-Null
    return $result
}

function Test-WduLifecycleRawProjection {
    param(
        [Parameter(Mandatory)][ValidateNotNull()][object] $Compiled,
        [Parameter(Mandatory)][ReadOnlyMemory[byte]] $Utf8Json
    )

    $bytes = $Utf8Json.ToArray()
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Fail-RawProjection '$' 'must not begin with a UTF-8 BOM'
    }
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    $options.AllowTrailingCommas = $false
    try { $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes), $options) }
    catch { Fail-RawProjection '$' "is not one valid UTF-8 JSON value: $($_.Exception.Message)" }
    try {
        $root = $document.RootElement
        Assert-NoDuplicateProperties $root '$'
        $rootProperties = @(Assert-ExactProperties $root $script:RootProperties '$')
        Assert-ExactString $rootProperties[0].Value $script:ProjectionSchema '$.schema'
        $artifactId = Assert-JsonString $rootProperties[1].Value '$.artifact'
        $artifact = Get-CompilerArtifact $Compiled $artifactId '$.artifact'
        Assert-ExactString $rootProperties[2].Value $script:CanonicalAnchor '$.canonical'
        Assert-ExactString $rootProperties[3].Value $Compiled.Digest '$.canonicalContentDigest'
        Assert-ExactString $rootProperties[4].Value $Compiled.AssignmentDigest '$.assignmentDigest'
        Assert-ExactCount $rootProperties[5].Value $Compiled.Counts.rowCount '$.rowCount'
        Assert-ExactCount $rootProperties[6].Value $Compiled.Counts.applicabilityKeyCount '$.applicabilityKeyCount'
        Assert-ExactCount $rootProperties[7].Value $Compiled.Counts.requirementCount '$.requirementCount'
        Assert-ExactCount $rootProperties[8].Value $Compiled.Counts.artifactCount '$.artifactCount'
        Assert-Requirements $rootProperties[9].Value @($Compiled.Requirements) '$.requirements'
        Assert-StringVector $rootProperties[10].Value @($Compiled.Artifacts | ForEach-Object { $_.Id }) '$.artifactIds'
        $assignmentProperties = @(Assert-ExactProperties $rootProperties[11].Value @('rowIds') '$.assignment')
        Assert-StringVector $assignmentProperties[0].Value @($artifact.RowIds) '$.assignment.rowIds'
        return [pscustomobject]@{ Artifact = $artifact.Id; Result = 'ExactMatch' }
    }
    finally { $document.Dispose() }
}

Export-ModuleMember -Function New-WduLifecycleRawProjection, Test-WduLifecycleRawProjection
