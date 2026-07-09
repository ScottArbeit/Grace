[CmdletBinding()]
param(
    [ValidateSet('All', 'Freshness', 'CanonicalVersion', 'Projections', 'Quality', 'GeneratedClientMatrix', 'SdkPackage', 'ProtocolVectors')]
    [string] $Check = 'All',

    [switch] $AllowPending
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Pending = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string] $Message)
    $script:Failures.Add($Message)
    Write-Host "[FAIL] $Message"
}

function Add-Pending {
    param([string] $Message)
    $script:Pending.Add($Message)

    if ($AllowPending) {
        Write-Host "[PENDING] $Message"
    }
    else {
        Add-Failure $Message
    }
}

function Add-Pass {
    param([string] $Message)
    Write-Host "[PASS] $Message"
}

function Get-RepoRoot {
    $root = git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
        throw 'Unable to resolve repository root with git rev-parse.'
    }

    return $root.Trim()
}

function Get-OpenApiRoot {
    param([string] $RepoRoot)
    return Join-Path $RepoRoot 'src/OpenAPI'
}

function Get-FileSha256 {
    param([string] $Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-StringSha256 {
    param([string] $Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return [System.Convert]::ToHexString($sha.ComputeHash($bytes)).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DirectoryManifestHash {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $null
    }

    $lines = Get-ChildItem -Path $Path -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(node_modules|target|dist|build|__pycache__|\.pytest_cache)\\' } |
        Where-Object { $_.Name -notin @('package-lock.json', 'Cargo.lock') } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            "$relative $hash"
        }

    $manifestText = ($lines -join "`n") + "`n"
    return Get-StringSha256 $manifestText
}

function Get-OpenApiManifest {
    param([string] $OpenApiRoot)

    $manifestPath = Join-Path $OpenApiRoot 'OpenAPI.ProofManifest.json'

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Failure "Missing OpenAPI proof manifest: $manifestPath"
        return $null
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -notin @(1, 2)) {
        Add-Failure "Unsupported OpenAPI proof manifest schemaVersion '$($manifest.schemaVersion)'."
    }

    return $manifest
}

function Get-GeneratedArtifactPaths {
    param([object] $Manifest)

    $artifactPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($null -eq $Manifest) {
        return $artifactPaths
    }

    $generatedArtifactsProperty = $Manifest.PSObject.Properties['generatedArtifacts']
    if ($null -eq $generatedArtifactsProperty -or $null -eq $generatedArtifactsProperty.Value) {
        return $artifactPaths
    }

    foreach ($artifact in $Manifest.generatedArtifacts) {
        [void] $artifactPaths.Add([string] $artifact.path)
    }

    return $artifactPaths
}

function Get-CanonicalSourceDigest {
    param([object[]] $CanonicalSourceFiles)

    $digestInput = ($CanonicalSourceFiles |
        Sort-Object path |
        ForEach-Object { "$($_.path):$($_.sha256)" }) -join "`n"

    return Get-StringSha256 $digestInput
}

function Get-OpenApiVersion {
    param([string] $Path)

    $firstLine = Get-Content -LiteralPath $Path -TotalCount 1
    $match = [regex]::Match($firstLine, '^openapi:\s*(?<version>\S+)\s*$')
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups['version'].Value
}

function Test-OpenApiFreshness {
    param([string] $RepoRoot)

    Write-Host '== OpenAPI freshness =='
    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $manifest = Get-OpenApiManifest $openApiRoot
    if ($null -eq $manifest) {
        return
    }

    $expectedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.canonicalSourceFiles) {
        [void] $expectedFiles.Add([string] $entry.path)
        $sourcePath = Join-Path $openApiRoot ([string] $entry.path)

        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            Add-Failure "Manifest source file is missing: $($entry.path)"
            continue
        }

        $actualHash = Get-FileSha256 $sourcePath
        if ($actualHash -ne ([string] $entry.sha256).ToLowerInvariant()) {
            Add-Failure "OpenAPI source hash is stale for $($entry.path). Expected $($entry.sha256), actual $actualHash."
        }
    }

    $generatedArtifactPaths = Get-GeneratedArtifactPaths $manifest
    $actualFiles = Get-ChildItem -LiteralPath $openApiRoot -Filter '*.OpenAPI.yaml' -File |
        Where-Object { -not $generatedArtifactPaths.Contains($_.Name) } |
        Sort-Object Name |
        ForEach-Object { $_.Name }

    foreach ($actualFile in $actualFiles) {
        if (-not $expectedFiles.Contains($actualFile)) {
            Add-Failure "OpenAPI source file is not represented in the proof manifest: $actualFile"
        }
    }

    if ($manifest.generatedArtifacts.Count -eq 0) {
        Write-Host '[INFO] No generated OpenAPI/client artifacts are declared; this scaffold accepts no generated-client freshness.'
    }
    else {
        $canonicalSourceDigest = Get-CanonicalSourceDigest @($manifest.canonicalSourceFiles)
        foreach ($artifact in $manifest.generatedArtifacts) {
            $artifactPath = Join-Path $openApiRoot ([string] $artifact.path)
            if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
                Add-Failure "Generated OpenAPI artifact is missing: $($artifact.path)"
                continue
            }

            $actualHash = Get-FileSha256 $artifactPath
            if ($actualHash -ne ([string] $artifact.sha256).ToLowerInvariant()) {
                Add-Failure "Generated OpenAPI artifact hash is stale for $($artifact.path). Expected $($artifact.sha256), actual $actualHash."
            }

            if ($null -eq $artifact.sourceDigest -or [string]::IsNullOrWhiteSpace([string] $artifact.sourceDigest)) {
                Add-Failure "Generated OpenAPI artifact is missing sourceDigest provenance: $($artifact.path)"
            }
            elseif (([string] $artifact.sourceDigest).ToLowerInvariant() -ne $canonicalSourceDigest) {
                Add-Failure "Generated OpenAPI artifact sourceDigest is stale for $($artifact.path). Expected $($artifact.sourceDigest), actual $canonicalSourceDigest."
            }

            if ($null -eq $artifact.generator -or [string]::IsNullOrWhiteSpace([string] $artifact.generator)) {
                Add-Failure "Generated OpenAPI artifact is missing generator provenance: $($artifact.path)"
            }

            if ($null -eq $artifact.generatorVersion -or [string]::IsNullOrWhiteSpace([string] $artifact.generatorVersion)) {
                Add-Failure "Generated OpenAPI artifact is missing generatorVersion provenance: $($artifact.path)"
            }

            if ($null -eq $artifact.command -or [string]::IsNullOrWhiteSpace([string] $artifact.command)) {
                Add-Failure "Generated OpenAPI artifact is missing regeneration command provenance: $($artifact.path)"
            }
        }
    }

    Add-Pass "OpenAPI proof manifest covers $($expectedFiles.Count) canonical source files."
}

function Test-OpenApiCanonicalVersion {
    param([string] $RepoRoot)

    Write-Host '== OpenAPI canonical version =='
    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $manifest = Get-OpenApiManifest $openApiRoot
    if ($null -eq $manifest) {
        return
    }

    $canonicalOpenApiVersionProperty = $manifest.PSObject.Properties['canonicalOpenApiVersion']
    if ($null -eq $canonicalOpenApiVersionProperty) {
        Add-Failure 'OpenAPI manifest is missing canonicalOpenApiVersion.'
    }
    elseif ($canonicalOpenApiVersionProperty.Value -ne '3.2.0') {
        Add-Failure "OpenAPI manifest canonicalOpenApiVersion is '$($canonicalOpenApiVersionProperty.Value)' instead of '3.2.0'."
    }

    foreach ($entry in $manifest.canonicalSourceFiles) {
        $sourcePath = Join-Path $openApiRoot ([string] $entry.path)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            continue
        }

        $version = Get-OpenApiVersion $sourcePath
        if ($null -ne $version -and $version -ne '3.2.0') {
            Add-Failure "Canonical OpenAPI entrypoint $($entry.path) declares '$version' instead of '3.2.0'."
        }
    }

    Add-Pass 'Canonical OpenAPI source declares OpenAPI 3.2.0.'
}

function Test-OpenApiProjections {
    param([string] $RepoRoot)

    Write-Host '== OpenAPI projections =='
    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $manifest = Get-OpenApiManifest $openApiRoot
    if ($null -eq $manifest) {
        return
    }

    $bundlePath = Join-Path $openApiRoot 'Grace.OpenAPI.yaml'
    $projectionPath = Join-Path $openApiRoot 'Grace.OpenAPI.3.1.2.yaml'
    $lossReportPath = Join-Path $openApiRoot 'Grace.OpenAPI.3.1.2.loss-report.json'
    $generatedArtifactPaths = Get-GeneratedArtifactPaths $manifest
    $requiredDerivedArtifacts = @(
        'Grace.OpenAPI.yaml',
        'Grace.OpenAPI.3.1.2.yaml',
        'Grace.OpenAPI.3.1.2.loss-report.json'
    )

    foreach ($requiredPath in @($bundlePath, $projectionPath, $lossReportPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            Add-Failure "Missing required OpenAPI derived artifact: $requiredPath"
            return
        }
    }

    foreach ($requiredArtifact in $requiredDerivedArtifacts) {
        if (-not $generatedArtifactPaths.Contains($requiredArtifact)) {
            Add-Failure "Required OpenAPI derived artifact is not represented in generatedArtifacts: $requiredArtifact"
        }
    }

    $bundleVersion = Get-OpenApiVersion $bundlePath
    if ($bundleVersion -ne '3.2.0') {
        Add-Failure "Canonical OpenAPI bundle declares '$bundleVersion' instead of '3.2.0'."
    }

    $projectionVersion = Get-OpenApiVersion $projectionPath
    if ($projectionVersion -ne '3.1.2') {
        Add-Failure "Generator compatibility projection declares '$projectionVersion' instead of '3.1.2'."
    }

    $bundleText = Get-Content -LiteralPath $bundlePath -Raw
    $projectionText = Get-Content -LiteralPath $projectionPath -Raw
    $reconstitutedCanonical = $projectionText -replace '\Aopenapi:\s*3\.1\.2', 'openapi: 3.2.0'
    if ($reconstitutedCanonical -ne $bundleText) {
        Add-Failure 'Generator projection drifted beyond the recorded OpenAPI version downgrade.'
    }

    $lossReport = Get-Content -LiteralPath $lossReportPath -Raw | ConvertFrom-Json
    $downgrade = @($lossReport.transformations | Where-Object { $_.kind -eq 'openapi-version-downgrade' })
    if ($downgrade.Count -ne 1) {
        Add-Failure 'Projection loss report must record exactly one openapi-version-downgrade transformation.'
    }

    if ($lossReport.projectionArtifact -ne 'Grace.OpenAPI.3.1.2.yaml') {
        Add-Failure "Projection loss report points at '$($lossReport.projectionArtifact)' instead of 'Grace.OpenAPI.3.1.2.yaml'."
    }

    if ($lossReport.lostSemantics.Count -lt 1) {
        Add-Failure 'Projection loss report must describe lost semantics.'
    }

    Add-Pass 'OpenAPI derived bundle, generator projection, and loss report are present and loss-aware.'
}

function Get-OpenApiOperations {
    param([string] $OpenApiRoot)

    $operations = [System.Collections.Generic.List[object]]::new()
    $methods = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($method in @('get', 'put', 'post', 'delete', 'patch', 'head', 'options', 'trace')) {
        [void] $methods.Add($method)
    }

    $pathFiles = Get-ChildItem -LiteralPath $OpenApiRoot -Filter '*.Paths.OpenAPI.yaml' -File | Sort-Object Name
    $mainPath = Join-Path $OpenApiRoot 'Main.OpenAPI.yaml'
    $files = @($pathFiles) + @(Get-Item -LiteralPath $mainPath)

    foreach ($file in $files) {
        $lines = Get-Content -LiteralPath $file.FullName
        $currentPath = $null
        $currentPathIndent = -1
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            $pathMatch = [regex]::Match($line, "^(?<indent>\s*)'?(?<path>/[^']*)'?:\s*$")
            if ($pathMatch.Success) {
                $currentPath = $pathMatch.Groups['path'].Value
                $currentPathIndent = $pathMatch.Groups['indent'].Value.Length
                continue
            }

            $methodMatch = [regex]::Match($line, '^(?<indent>\s+)(?<method>get|put|post|delete|patch|head|options|trace):\s*$')
            if (-not $methodMatch.Success -or $null -eq $currentPath) {
                continue
            }

            $methodIndent = $methodMatch.Groups['indent'].Value.Length
            if ($methodIndent -le $currentPathIndent) {
                continue
            }

            $method = $methodMatch.Groups['method'].Value
            if (-not $methods.Contains($method)) {
                continue
            }

            $operationLines = [System.Collections.Generic.List[string]]::new()
            for ($cursor = $index + 1; $cursor -lt $lines.Count; $cursor++) {
                $nextLine = $lines[$cursor]
                $nextPathMatch = [regex]::Match($nextLine, "^(?<indent>\s*)'?(?<path>/[^']*)'?:\s*$")
                if ($nextPathMatch.Success -and $nextPathMatch.Groups['indent'].Value.Length -le $currentPathIndent) {
                    break
                }

                $nextMethodMatch = [regex]::Match($nextLine, '^(?<indent>\s+)(get|put|post|delete|patch|head|options|trace):\s*$')
                if ($nextMethodMatch.Success -and $nextMethodMatch.Groups['indent'].Value.Length -eq $methodIndent) {
                    break
                }

                $operationLines.Add($nextLine)
            }

            $operationText = $operationLines -join [Environment]::NewLine
            $operationIdMatch = [regex]::Match($operationText, '(?m)^\s+operationId:\s*(?<id>[A-Za-z][A-Za-z0-9_]*)\s*$')
            $responsesMatch = [regex]::Match($operationText, '(?m)^\s+responses:\s*$')

            $operations.Add([pscustomobject]@{
                File = $file.Name
                Path = $currentPath
                Method = $method
                LineNumber = $index + 1
                OperationId = if ($operationIdMatch.Success) { $operationIdMatch.Groups['id'].Value } else { $null }
                OperationText = $operationText
                HasTag = $operationText -match "(?m)^\s+tags:\s*$" -and $operationText -match "(?m)^\s+-\s+[A-Za-z][A-Za-z0-9 ._-]*\s*$"
                HasResponses = $responsesMatch.Success
                Has400 = $operationText -match "(?m)^\s+'400':\s*$"
                Has500 = $operationText -match "(?m)^\s+'500':\s*$"
            })
        }
    }

    return $operations
}

function Get-OpenApiMethodDeclarationCount {
    param([string] $OpenApiRoot)

    $count = 0
    $pathFiles = Get-ChildItem -LiteralPath $OpenApiRoot -Filter '*.Paths.OpenAPI.yaml' -File | Sort-Object Name
    $mainPath = Join-Path $OpenApiRoot 'Main.OpenAPI.yaml'
    $files = @($pathFiles) + @(Get-Item -LiteralPath $mainPath)

    foreach ($file in $files) {
        $lines = Get-Content -LiteralPath $file.FullName
        $currentPathIndent = -1

        foreach ($line in $lines) {
            $pathMatch = [regex]::Match($line, "^(?<indent>\s*)'?(?<path>/[^']*)'?:\s*$")
            if ($pathMatch.Success) {
                $currentPathIndent = $pathMatch.Groups['indent'].Value.Length
                continue
            }

            $methodMatch = [regex]::Match($line, '^(?<indent>\s+)(get|put|post|delete|patch|head|options|trace):\s*$')
            if ($methodMatch.Success -and $methodMatch.Groups['indent'].Value.Length -gt $currentPathIndent) {
                $count++
            }
        }
    }

    return $count
}

function Assert-TextContains {
    param(
        [string] $Text,
        [string] $Needle,
        [string] $Message
    )

    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        Add-Failure $Message
    }
}

function Get-RequiredOpenApiOperation {
    param(
        [object[]] $Operations,
        [string] $File,
        [string] $OperationId
    )

    $matches = @($Operations | Where-Object { $_.File -eq $File -and $_.OperationId -eq $OperationId })
    if ($matches.Count -eq 1) {
        return $matches[0]
    }

    Add-Failure "Expected exactly one OpenAPI operation '$OperationId' in $File, found $($matches.Count)."
    return $null
}

function Assert-OperationTextMatches {
    param(
        [object] $Operation,
        [string] $Pattern,
        [string] $Message
    )

    if ($null -eq $Operation) {
        return
    }

    if ([string] $Operation.OperationText -notmatch $Pattern) {
        Add-Failure $Message
    }
}

function Get-OpenApiNamedBlock {
    param(
        [string] $Text,
        [string] $Name
    )

    $lines = [regex]::Split($Text, '\r?\n')
    $blockLinePattern = "^(?<indent>\s*)$([regex]::Escape($Name)):\s*(?:#.*)?$"
    $blockStart = -1
    $blockIndent = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $match = [regex]::Match($lines[$i], $blockLinePattern)
        if ($match.Success) {
            $blockStart = $i
            $blockIndent = $match.Groups['indent'].Value.Length
            break
        }
    }

    if ($blockStart -lt 0) {
        return $null
    }

    $blockEnd = $lines.Count
    for ($i = $blockStart + 1; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) {
            continue
        }

        $indent = Get-YamlIndentLength $lines[$i]
        if ($indent -le $blockIndent) {
            $blockEnd = $i
            break
        }
    }

    return [pscustomobject]@{
        Lines = @($lines[$blockStart..($blockEnd - 1)])
        Text = ($lines[$blockStart..($blockEnd - 1)] -join [Environment]::NewLine)
        Indent = $blockIndent
    }
}

function Assert-OpenApiResponseIsRawStringDictionary {
    param(
        [string] $Text,
        [string] $ResponseName,
        [string] $MessagePrefix
    )

    $responseBlock = Get-OpenApiNamedBlock $Text $ResponseName
    if ($null -eq $responseBlock) {
        Add-Failure "$MessagePrefix response component is missing."
        return
    }

    $responseText = [string] $responseBlock.Text
    foreach ($forbiddenProperty in @('ReturnValue', 'EventTime', 'CorrelationId', 'Properties')) {
        if ($responseText -match "(?m)^\s+$([regex]::Escape($forbiddenProperty)):\s*") {
            Add-Failure "$MessagePrefix response must be a raw id-to-name dictionary, not an envelope or DTO array."
            return
        }
    }

    foreach ($forbiddenReference in @('OrganizationDto', 'RepositoryDto')) {
        if ($responseText.Contains($forbiddenReference, [StringComparison]::Ordinal)) {
            Add-Failure "$MessagePrefix response must be a raw id-to-name dictionary, not an envelope or DTO array."
            return
        }
    }

    if ($responseText -notmatch "(?m)^\s+schema:\s*$" -or
        $responseText -notmatch "(?m)^\s+type:\s*object\s*$" -or
        $responseText -notmatch "(?m)^\s+additionalProperties:\s*$" -or
        $responseText -notmatch "(?m)^\s+type:\s*string\s*$") {
        Add-Failure "$MessagePrefix response must model application/json as a raw object with string additionalProperties."
    }
}

function Assert-OpenApiNamedBlockDoesNotContain {
    param(
        [string] $Text,
        [string] $BlockName,
        [string] $ForbiddenNeedle,
        [string] $Message
    )

    $block = Get-OpenApiNamedBlock $Text $BlockName
    if ($null -eq $block) {
        Add-Failure "OpenAPI block '$BlockName' is missing."
        return
    }

    if ([string] $block.Text -match "(?m)^\s+$([regex]::Escape($ForbiddenNeedle)):\s*") {
        Add-Failure $Message
    }
}

function Assert-OpenApiSchemaHasProperty {
    param(
        [string] $Text,
        [string] $SchemaName,
        [string] $PropertyName,
        [string] $Message
    )

    $propertyBlock = Get-OpenApiSchemaPropertyBlock $Text $SchemaName $PropertyName
    if ($null -eq $propertyBlock) {
        Add-Failure $Message
    }
}

function Assert-OpenApiSchemaRequiresProperties {
    param(
        [string] $Text,
        [string] $SchemaName,
        [string[]] $RequiredProperties,
        [string] $MessagePrefix
    )

    $block = Get-OpenApiNamedBlock $Text $SchemaName
    if ($null -eq $block) {
        Add-Failure "OpenAPI schema '$SchemaName' is missing."
        return
    }

    $requiredLine = @($block.Lines | Where-Object { $_ -match '^\s+required:\s*\[(?<items>[^\]]*)\]\s*$' } | Select-Object -First 1)
    if ($requiredLine.Count -eq 0) {
        Add-Failure "$MessagePrefix must declare required properties."
        return
    }

    $match = [regex]::Match([string] $requiredLine[0], '^\s+required:\s*\[(?<items>[^\]]*)\]\s*$')
    $actualRequired = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in ($match.Groups['items'].Value -split ',')) {
        [void] $actualRequired.Add($item.Trim())
    }

    foreach ($requiredProperty in $RequiredProperties) {
        if (-not $actualRequired.Contains($requiredProperty)) {
            Add-Failure "$MessagePrefix must require $requiredProperty."
        }
    }
}

function Assert-MaterializationStringEnumSchema {
    param(
        [string] $Text,
        [string] $SchemaName,
        [string[]] $ExpectedValues
    )

    $block = Get-OpenApiNamedBlock $Text $SchemaName
    if ($null -eq $block) {
        Add-Failure "Materialization enum schema '$SchemaName' is missing."
        return
    }

    $blockText = [string] $block.Text
    if ($blockText -notmatch '(?m)^\s+type:\s*string\s*$') {
        Add-Failure "Materialization enum schema '$SchemaName' must use the string wire format."
    }

    if ($blockText -match '(?m)^\s+type:\s*integer\s*$' -or $blockText -match '(?m)^\s+enum:\s*\[\s*\d') {
        Add-Failure "Materialization enum schema '$SchemaName' must not advertise integer values."
    }

    $enumLine = @($block.Lines | Where-Object { $_ -match '^\s+enum:\s*\[(?<items>[^\]]*)\]\s*$' } | Select-Object -First 1)
    if ($enumLine.Count -eq 0) {
        Add-Failure "Materialization enum schema '$SchemaName' must declare enum values."
        return
    }

    $match = [regex]::Match([string] $enumLine[0], '^\s+enum:\s*\[(?<items>[^\]]*)\]\s*$')
    $actualValues = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in ($match.Groups['items'].Value -split ',')) {
        [void] $actualValues.Add($item.Trim())
    }

    foreach ($expectedValue in $ExpectedValues) {
        if (-not $actualValues.Contains($expectedValue)) {
            Add-Failure "Materialization enum schema '$SchemaName' is missing string value '$expectedValue'."
        }
    }
}

function Get-YamlIndentLength {
    param([string] $Line)

    $match = [regex]::Match($Line, '^(?<indent>\s*)')
    return $match.Groups['indent'].Value.Length
}

function Get-OpenApiSchemaPropertyBlock {
    param(
        [string] $Text,
        [string] $SchemaName,
        [string] $PropertyName
    )

    $lines = [regex]::Split($Text, '\r?\n')
    $schemaLinePattern = "^(?<indent>\s*)$([regex]::Escape($SchemaName)):\s*(?:#.*)?$"
    $schemaStart = -1
    $schemaIndent = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $match = [regex]::Match($lines[$i], $schemaLinePattern)
        if ($match.Success) {
            $schemaStart = $i
            $schemaIndent = $match.Groups['indent'].Value.Length
            break
        }
    }

    if ($schemaStart -lt 0) {
        return $null
    }

    $schemaEnd = $lines.Count
    for ($i = $schemaStart + 1; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) {
            continue
        }

        $indent = Get-YamlIndentLength $lines[$i]
        if ($indent -le $schemaIndent) {
            $schemaEnd = $i
            break
        }
    }

    $propertiesStart = -1
    $propertiesIndent = $schemaIndent + 2
    $propertiesLinePattern = "^\s{$propertiesIndent}properties:\s*(?:#.*)?$"
    for ($i = $schemaStart + 1; $i -lt $schemaEnd; $i++) {
        if ([regex]::IsMatch($lines[$i], $propertiesLinePattern)) {
            $propertiesStart = $i
            break
        }
    }

    if ($propertiesStart -lt 0) {
        return $null
    }

    $propertiesEnd = $schemaEnd
    for ($i = $propertiesStart + 1; $i -lt $schemaEnd; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) {
            continue
        }

        $indent = Get-YamlIndentLength $lines[$i]
        if ($indent -le $propertiesIndent) {
            $propertiesEnd = $i
            break
        }
    }

    $propertyStart = -1
    $propertyIndent = $propertiesIndent + 2
    $propertyLinePattern = "^\s{$propertyIndent}$([regex]::Escape($PropertyName)):\s*(?:#.*)?$"
    for ($i = $propertiesStart + 1; $i -lt $propertiesEnd; $i++) {
        if ([regex]::IsMatch($lines[$i], $propertyLinePattern)) {
            $propertyStart = $i
            break
        }
    }

    if ($propertyStart -lt 0) {
        return $null
    }

    $propertyEnd = $propertiesEnd
    for ($i = $propertyStart + 1; $i -lt $propertiesEnd; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) {
            continue
        }

        $indent = Get-YamlIndentLength $lines[$i]
        if ($indent -le $propertyIndent) {
            $propertyEnd = $i
            break
        }
    }

    return [pscustomobject]@{
        Lines = @($lines[$propertyStart..($propertyEnd - 1)])
        PropertyIndent = $propertyIndent
    }
}

function Assert-OpenApiSchemaPropertyIsString {
    param(
        [string] $Text,
        [string] $SchemaName,
        [string] $PropertyName,
        [string] $Message
    )

    $propertyBlock = Get-OpenApiSchemaPropertyBlock $Text $SchemaName $PropertyName
    if ($null -eq $propertyBlock) {
        Add-Failure $Message
        return
    }

    $directChildIndent = $propertyBlock.PropertyIndent + 2
    $directTypeValues = [System.Collections.Generic.List[string]]::new()
    $hasDirectRef = $false

    foreach ($line in @($propertyBlock.Lines | Select-Object -Skip 1)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $indent = Get-YamlIndentLength $line
        if ($indent -ne $directChildIndent) {
            continue
        }

        $typeMatch = [regex]::Match($line, "^\s{$directChildIndent}type:\s*['""]?(?<type>[^'""\s#]+)['""]?\s*(?:#.*)?$")
        if ($typeMatch.Success) {
            $directTypeValues.Add($typeMatch.Groups['type'].Value)
        }

        if ([regex]::IsMatch($line, "^\s{$directChildIndent}\`$ref:\s*")) {
            $hasDirectRef = $true
        }
    }

    if ($directTypeValues.Count -ne 1 -or $directTypeValues[0] -ne 'string' -or $hasDirectRef) {
        Add-Failure $Message
    }
}

function Assert-OperationHasJsonRequestExample {
    param(
        [object] $Operation,
        [string] $Message
    )

    if ($null -eq $Operation) {
        return
    }

    if ([string] $Operation.OperationText -match '(?m)^\s+requestBody:\s*$') {
        Assert-OperationTextMatches `
            $Operation `
            '(?s)requestBody:\s*.*?content:\s*.*?application/json:\s*.*?examples:' `
            $Message
    }
}

function Assert-OperationSha256ExamplesAreValid {
    param(
        [object] $Operation,
        [string[]] $RequiredFields,
        [string] $Pattern = '^[A-Fa-f0-9]{64}$',
        [string] $ValidationLabel = 'valid 64-character SHA-256 hex value'
    )

    if ($null -eq $Operation) {
        return
    }

    $usesDefaultValidation = -not $PSBoundParameters.ContainsKey('Pattern')
    $isSha256PrefixLookup = $Operation.OperationId -in @('GetDiffBySha256Hash', 'GetDirectoryVersionBySha256Hash')
    if ($usesDefaultValidation -and $isSha256PrefixLookup) {
        $Pattern = '^[A-Fa-f0-9]{2,64}$'
        $ValidationLabel = 'valid 2- to 64-character SHA-256 prefix'
    }

    $location = "$($Operation.File):$($Operation.LineNumber) $($Operation.Method.ToUpperInvariant()) $($Operation.Path)"
    $exampleMatches = [regex]::Matches(
        [string] $Operation.OperationText,
        "(?m)^\s+(?<name>Sha256Hash(?:1|2)?):\s*(?<value>['""]?[A-Za-z0-9]+['""]?)\s*$"
    )
    $fieldsSeen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($match in $exampleMatches) {
        $fieldName = $match.Groups['name'].Value
        $fieldValue = $match.Groups['value'].Value.Trim("'`"")
        [void] $fieldsSeen.Add($fieldName)

        if ($fieldValue -notmatch $Pattern) {
            Add-Failure ("OpenAPI example field '{0}' must be a {1}: {2} has '{3}'." -f $fieldName, $ValidationLabel, $location, $fieldValue)
        }
    }

    foreach ($requiredField in $RequiredFields) {
        if (-not $fieldsSeen.Contains($requiredField)) {
            Add-Failure "OpenAPI operation '$($Operation.OperationId)' must include a literal '$requiredField' example for S05 SHA-256 proof coverage: $location"
        }
    }
}

function Test-OpenApiBranchReferenceDiffDetails {
    param(
        [string] $OpenApiRoot,
        [object[]] $Operations
    )

    $expectedOperations = @(
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'CreateBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'RebaseBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'PromoteBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'CommitBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'CheckpointBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'SaveBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'TagBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'EnableBranchPromotion'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'EnableBranchCommit'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'EnableBranchCheckpoint'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'EnableBranchSave'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'EnableBranchTag'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'DeleteBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'GetBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'GetParentBranch'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'GetBranchReference'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'ListBranchReferences'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'ListBranchPromotions'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'ListBranchCommits'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'ListBranchCheckpoints'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'ListBranchSaves'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Branch.Paths.OpenAPI.yaml'; OperationId = 'ListBranchTags'; Tag = 'Branches' },
        [pscustomobject]@{ File = 'Diff.Paths.OpenAPI.yaml'; OperationId = 'PopulateDiff'; Tag = 'Diffs' },
        [pscustomobject]@{ File = 'Diff.Paths.OpenAPI.yaml'; OperationId = 'GetDiff'; Tag = 'Diffs' },
        [pscustomobject]@{ File = 'Diff.Paths.OpenAPI.yaml'; OperationId = 'GetDiffBySha256Hash'; Tag = 'Diffs' },
        [pscustomobject]@{ File = 'Diff.Paths.OpenAPI.yaml'; OperationId = 'GetDiffByBlake3Hash'; Tag = 'Diffs' }
    )

    $branchCommandOperationIds = @(
        'CreateBranch',
        'RebaseBranch',
        'PromoteBranch',
        'CommitBranch',
        'CheckpointBranch',
        'SaveBranch',
        'TagBranch',
        'EnableBranchPromotion',
        'EnableBranchCommit',
        'EnableBranchCheckpoint',
        'EnableBranchSave',
        'EnableBranchTag',
        'DeleteBranch'
    )

    $branchQueryResponses = @(
        [pscustomobject]@{ OperationId = 'GetBranch'; Response = 'BranchResponse' },
        [pscustomobject]@{ OperationId = 'GetParentBranch'; Response = 'BranchResponse' },
        [pscustomobject]@{ OperationId = 'GetBranchReference'; Response = 'ReferenceResponse' },
        [pscustomobject]@{ OperationId = 'ListBranchReferences'; Response = 'ReferenceListResponse' },
        [pscustomobject]@{ OperationId = 'ListBranchPromotions'; Response = 'ReferenceListResponse' },
        [pscustomobject]@{ OperationId = 'ListBranchCommits'; Response = 'ReferenceListResponse' },
        [pscustomobject]@{ OperationId = 'ListBranchCheckpoints'; Response = 'ReferenceListResponse' },
        [pscustomobject]@{ OperationId = 'ListBranchSaves'; Response = 'ReferenceListResponse' },
        [pscustomobject]@{ OperationId = 'ListBranchTags'; Response = 'ReferenceListResponse' }
    )

    $familyFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @('Branch.Paths.OpenAPI.yaml', 'Diff.Paths.OpenAPI.yaml')) {
        [void] $familyFiles.Add($file)
    }

    $familyOperations = @($Operations | Where-Object { $familyFiles.Contains($_.File) })
    $genericOperationIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($operationId in @('Create', 'Get', 'Delete', 'List', 'Update', 'Enable', 'Disable', 'Promote', 'Rebase')) {
        [void] $genericOperationIds.Add($operationId)
    }

    foreach ($operation in $familyOperations) {
        $location = "$($operation.File):$($operation.LineNumber) $($operation.Method.ToUpperInvariant()) $($operation.Path)"

        if ([string]::IsNullOrWhiteSpace([string] $operation.OperationId)) {
            Add-Failure "Branch/diff OpenAPI operation is missing an SDK-friendly operationId: $location"
            continue
        }

        if ($genericOperationIds.Contains([string] $operation.OperationId)) {
            Add-Failure "Branch/diff OpenAPI operationId '$($operation.OperationId)' is too generic for SDK generation: $location"
        }

        if (-not $operation.HasTag) {
            Add-Failure "Branch/diff OpenAPI operation is missing its primary SDK tag: $location"
        }

        if (-not ($operation.Has400 -and $operation.Has500)) {
            Add-Failure "Branch/diff OpenAPI operation is missing reusable 400/500 error responses: $location"
        }

        Assert-OperationTextMatches `
            $operation `
            "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./(?:Branch|Diff)\.Components\.OpenAPI\.yaml#/" `
            "Branch/diff OpenAPI operation must use a family success response component: $location"

        Assert-OperationHasJsonRequestExample `
            $operation `
            "Branch/diff OpenAPI operation with a JSON request body must include an example: $location"

        Assert-OperationSha256ExamplesAreValid $operation @()
    }

    foreach ($expected in $expectedOperations) {
        $operation = Get-RequiredOpenApiOperation $Operations $expected.File $expected.OperationId

        Assert-OperationTextMatches `
            $operation `
            "(?s)tags:\s*-\s+$($expected.Tag)\b" `
            "Operation '$($expected.OperationId)' must carry primary SDK tag '$($expected.Tag)'."
    }

    $promoteBranchOperation = Get-RequiredOpenApiOperation $Operations 'Branch.Paths.OpenAPI.yaml' 'PromoteBranch'
    Assert-OperationSha256ExamplesAreValid $promoteBranchOperation @('Sha256Hash')

    $getDiffBySha256HashOperation = Get-RequiredOpenApiOperation $Operations 'Diff.Paths.OpenAPI.yaml' 'GetDiffBySha256Hash'
    Assert-OperationSha256ExamplesAreValid $getDiffBySha256HashOperation @('Sha256Hash1', 'Sha256Hash2')

    $getDiffByBlake3HashOperation = Get-RequiredOpenApiOperation $Operations 'Diff.Paths.OpenAPI.yaml' 'GetDiffByBlake3Hash'

    foreach ($operationId in $branchCommandOperationIds) {
        $operation = Get-RequiredOpenApiOperation $Operations 'Branch.Paths.OpenAPI.yaml' $operationId

        Assert-OperationTextMatches `
            $operation `
            "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./Branch\.Components\.OpenAPI\.yaml#/BranchCommandResponse'" `
            "Branch command operation '$operationId' must use the string-returning BranchCommandResponse envelope."

        if ($null -ne $operation -and [string] $operation.OperationText -match 'Branch\.Components\.OpenAPI\.yaml#/BranchResponse') {
            Add-Failure "Branch command operation '$operationId' must not use the BranchApiDto BranchResponse envelope."
        }
    }

    foreach ($expectedResponse in $branchQueryResponses) {
        $operation = Get-RequiredOpenApiOperation $Operations 'Branch.Paths.OpenAPI.yaml' $expectedResponse.OperationId

        Assert-OperationTextMatches `
            $operation `
            "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./Branch\.Components\.OpenAPI\.yaml#/$($expectedResponse.Response)'" `
            "Branch query operation '$($expectedResponse.OperationId)' must use '$($expectedResponse.Response)' instead of a command string envelope."

        if ($null -ne $operation -and [string] $operation.OperationText -match 'Branch\.Components\.OpenAPI\.yaml#/BranchCommandResponse') {
            Add-Failure "Branch query operation '$($expectedResponse.OperationId)' must not use the string-returning BranchCommandResponse envelope."
        }
    }

    $populateDiffOperation = Get-RequiredOpenApiOperation $Operations 'Diff.Paths.OpenAPI.yaml' 'PopulateDiff'
    Assert-OperationTextMatches `
        $populateDiffOperation `
        "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./Diff\.Components\.OpenAPI\.yaml#/DiffPopulateResponse'" `
        "PopulateDiff must use the string-returning DiffPopulateResponse envelope."

    $branchComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Branch.Components.OpenAPI.yaml') -Raw
    foreach ($requiredBranchContract in @(
            'DirectoryVersionId:',
            'BranchCommandReturnValue:',
            'BranchCommandResponse:',
            'BranchReturnValue:',
            'ReferenceReturnValue:',
            'ReferenceListReturnValue:'
        )) {
        Assert-TextContains $branchComponentsText $requiredBranchContract "Branch OpenAPI components are missing '$requiredBranchContract'."
    }

    Assert-OpenApiSchemaPropertyIsString `
        $branchComponentsText `
        'BranchCommandReturnValue' `
        'ReturnValue' `
        'BranchCommandReturnValue must model command success ReturnValue as a string.'

    Assert-OperationTextMatches `
        ([pscustomobject]@{ OperationText = $branchComponentsText }) `
        "(?s)BranchReturnValue:\s*.*?ReturnValue:\s*.*?\`$ref:\s*'#/BranchApiDto'" `
        'BranchReturnValue must keep branch query ReturnValue as BranchApiDto.'

    $diffComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Diff.Components.OpenAPI.yaml') -Raw
    foreach ($requiredDiffContract in @(
            'DirectoryVersionId1:',
            'DirectoryVersionId2:',
            'DiffReturnValue:',
            'DiffPopulateReturnValue:'
        )) {
        Assert-TextContains $diffComponentsText $requiredDiffContract "Diff OpenAPI components are missing '$requiredDiffContract'."
    }

    Assert-OpenApiSchemaPropertyIsString `
        $diffComponentsText `
        'DiffPopulateReturnValue' `
        'ReturnValue' `
        'DiffPopulateReturnValue must model populate success ReturnValue as a string.'

    if ($diffComponentsText -match '(?s)DiffPopulateReturnValue:\s*.*?ReturnValue:\s*.*?type:\s*boolean') {
        Add-Failure 'DiffPopulateReturnValue must not model populate success ReturnValue as boolean.'
    }

    $referenceComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Reference.Components.OpenAPI.yaml') -Raw
    foreach ($requiredReferenceContract in @(
            'ReferenceParameters:',
            'ReferenceId:',
            'ReferenceType:',
            'ReferenceText:'
        )) {
        Assert-TextContains $referenceComponentsText $requiredReferenceContract "Reference OpenAPI components are missing '$requiredReferenceContract'."
    }

    if ($referenceComponentsText -match '(RepositoryText|ReferenceCommand|ReferenceEvent|Actor)') {
        Add-Failure 'Reference OpenAPI components must expose public selector fields, not internal actor command/event shapes or stale RepositoryText naming.'
    }

    $branchComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Branch.Components.OpenAPI.yaml') -Raw
    foreach ($requiredAnnotationReferenceTypeContract in @(
            '$ref: ''Shared.Components.OpenAPI.yaml#/components/schemas/ReferenceType''',
            'ReferenceTypeFilter:',
            '- commit',
            '- save'
        )) {
        Assert-TextContains $branchComponentsText $requiredAnnotationReferenceTypeContract "Branch annotation OpenAPI contract must publish ReferenceType wire values and shared ReferenceType references; missing '$requiredAnnotationReferenceTypeContract'."
    }

    foreach ($staleAnnotationReferenceType in @('- Commit', '- Save')) {
        if ($branchComponentsText.Contains($staleAnnotationReferenceType, [StringComparison]::Ordinal)) {
            Add-Failure "Branch annotation OpenAPI examples must use ReferenceType JSON wire values, not F# case-name item '$staleAnnotationReferenceType'."
        }
    }

    $branchPathsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Branch.Paths.OpenAPI.yaml') -Raw
    $createBranchOperation = Get-RequiredOpenApiOperation $Operations 'Branch.Paths.OpenAPI.yaml' 'CreateBranch'
    $createBranchOperationText = if ($null -eq $createBranchOperation) { '' } else { [string] $createBranchOperation.OperationText }

    foreach ($requiredCreateBranchReferenceTypeValue in @(
            '- commit',
            '- checkpoint',
            '- save',
            '- tag'
        )) {
        Assert-TextContains $createBranchOperationText $requiredCreateBranchReferenceTypeValue "Branch create OpenAPI path example must publish ReferenceType wire value '$requiredCreateBranchReferenceTypeValue'."
    }

    foreach ($staleCreateBranchReferenceTypeValue in @('- Commit', '- Checkpoint', '- Save', '- Tag')) {
        if ($createBranchOperationText.Contains($staleCreateBranchReferenceTypeValue, [StringComparison]::Ordinal)) {
            Add-Failure "Branch create OpenAPI path example must use ReferenceType JSON wire values, not F# case-name item '$staleCreateBranchReferenceTypeValue'."
        }
    }

    foreach ($requiredAnnotatePathReferenceTypeContract in @(
            'ReferenceTypes:',
            '- commit',
            '- save'
        )) {
        Assert-TextContains $branchPathsText $requiredAnnotatePathReferenceTypeContract "Branch annotate OpenAPI path example must publish ReferenceType wire values; missing '$requiredAnnotatePathReferenceTypeContract'."
    }

    $sharedComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Shared.Components.OpenAPI.yaml') -Raw
    $referenceTypeSchemaMatch = [regex]::Match($sharedComponentsText, '(?m)^    ReferenceType:\r?\n      type:\s*string\r?\n      enum:\s*\[(?<values>[^\]]+)\]')
    if (-not $referenceTypeSchemaMatch.Success) {
        Add-Failure 'Shared ReferenceType OpenAPI schema must declare a string enum.'
    }
    else {
        $referenceTypeValues =
            $referenceTypeSchemaMatch.Groups['values'].Value.Split(',') |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

        $expectedReferenceTypeWireValues = @('promotion', 'commit', 'checkpoint', 'save', 'tag', 'external', 'rebase')
        foreach ($requiredReferenceType in $expectedReferenceTypeWireValues) {
            if ($requiredReferenceType -cnotin $referenceTypeValues) {
                Add-Failure "Shared ReferenceType OpenAPI enum is missing server-supported wire value '$requiredReferenceType'."
            }
        }

        foreach ($staleReferenceTypeName in @('Promotion', 'Commit', 'Checkpoint', 'Save', 'Tag', 'External', 'Rebase')) {
            if ($staleReferenceTypeName -cin $referenceTypeValues) {
                Add-Failure "Shared ReferenceType OpenAPI enum must publish JSON wire value '$($staleReferenceTypeName.Substring(0, 1).ToLowerInvariant())$($staleReferenceTypeName.Substring(1))', not F# case name '$staleReferenceTypeName'."
            }
        }

        $unexpectedReferenceTypeValues = @($referenceTypeValues | Where-Object { $_ -cnotin $expectedReferenceTypeWireValues })
        if ($unexpectedReferenceTypeValues.Count -gt 0) {
            Add-Failure "Shared ReferenceType OpenAPI enum contains unexpected value(s): $($unexpectedReferenceTypeValues -join ', ')."
        }
    }
}

function Test-OpenApiOwnerOrganizationRepositoryDirectoryDetails {
    param(
        [string] $OpenApiRoot,
        [object[]] $Operations
    )

    $expectedOperations = @(
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'CreateOwner'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'SetOwnerName'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'SetOwnerType'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'SetOwnerSearchVisibility'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'SetOwnerDescription'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'ListOwnerOrganizations'; Tag = 'Owners'; Response = 'OwnerOrganizationsResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'DeleteOwner'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'UndeleteOwner'; Tag = 'Owners'; Response = 'OwnerCommandResponse' },
        [pscustomobject]@{ File = 'Owner.Paths.OpenAPI.yaml'; OperationId = 'GetOwner'; Tag = 'Owners'; Response = 'OwnerResponse' },

        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'CreateOrganization'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'SetOrganizationName'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'SetOrganizationType'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'SetOrganizationSearchVisibility'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'SetOrganizationDescription'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'ListOrganizationRepositories'; Tag = 'Organizations'; Response = 'OrganizationRepositoriesResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'DeleteOrganization'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'UndeleteOrganization'; Tag = 'Organizations'; Response = 'OrganizationCommandResponse' },
        [pscustomobject]@{ File = 'Organization.Paths.OpenAPI.yaml'; OperationId = 'GetOrganization'; Tag = 'Organizations'; Response = 'OrganizationResponse' },

        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'CreateRepository'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryVisibility'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryName'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositorySaveDays'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryCheckpointDays'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryStatus'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryDefaultServerApiVersion'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryRecordSaves'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'SetRepositoryDescription'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'DeleteRepository'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'UndeleteRepository'; Tag = 'Repositories'; Response = 'RepositoryCommandResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'RepositoryExists'; Tag = 'Repositories'; Response = 'RepositoryBooleanResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'RepositoryIsEmpty'; Tag = 'Repositories'; Response = 'RepositoryBooleanResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'GetRepository'; Tag = 'Repositories'; Response = 'RepositoryResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'ListRepositoryBranches'; Tag = 'Repositories'; Response = 'RepositoryBranchesResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'ListRepositoryReferencesByReferenceId'; Tag = 'Repositories'; Response = 'RepositoryReferencesResponse' },
        [pscustomobject]@{ File = 'Repository.Paths.OpenAPI.yaml'; OperationId = 'ListRepositoryBranchesByBranchId'; Tag = 'Repositories'; Response = 'RepositoryBranchesResponse' },

        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'CreateDirectoryVersion'; Tag = 'Directories'; Response = 'DirectoryCommandResponse' },
        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'GetDirectoryVersion'; Tag = 'Directories'; Response = 'DirectoryVersionResponse' },
        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'ListDirectoryVersionsRecursive'; Tag = 'Directories'; Response = 'DirectoryVersionListResponse' },
        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'ListDirectoryVersionsById'; Tag = 'Directories'; Response = 'DirectoryVersionListResponse' },
        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'GetDirectoryVersionBySha256Hash'; Tag = 'Directories'; Response = 'DirectoryVersionSha256HashLookupResponse' },
        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'GetDirectoryVersionByBlake3Hash'; Tag = 'Directories'; Response = 'DirectoryVersionHashLookupResponse' },
        [pscustomobject]@{ File = 'Directory.Paths.OpenAPI.yaml'; OperationId = 'SaveDirectoryVersions'; Tag = 'Directories'; Response = 'DirectoryCommandResponse' }
    )

    $familyFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @('Owner.Paths.OpenAPI.yaml', 'Organization.Paths.OpenAPI.yaml', 'Repository.Paths.OpenAPI.yaml', 'Directory.Paths.OpenAPI.yaml')) {
        [void] $familyFiles.Add($file)
    }

    $genericOperationIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($operationId in @('Create', 'Get', 'Delete', 'List', 'Update', 'Set', 'Exists', 'IsEmpty', 'Save')) {
        [void] $genericOperationIds.Add($operationId)
    }

    foreach ($operation in @($Operations | Where-Object { $familyFiles.Contains($_.File) })) {
        $location = "$($operation.File):$($operation.LineNumber) $($operation.Method.ToUpperInvariant()) $($operation.Path)"

        if ([string]::IsNullOrWhiteSpace([string] $operation.OperationId)) {
            Add-Failure "Owner/organization/repository/directory OpenAPI operation is missing an SDK-friendly operationId: $location"
            continue
        }

        if ($genericOperationIds.Contains([string] $operation.OperationId)) {
            Add-Failure "Owner/organization/repository/directory OpenAPI operationId '$($operation.OperationId)' is too generic for SDK generation: $location"
        }

        if (-not $operation.HasTag) {
            Add-Failure "Owner/organization/repository/directory OpenAPI operation is missing its primary SDK tag: $location"
        }

        if (-not ($operation.Has400 -and $operation.Has500)) {
            Add-Failure "Owner/organization/repository/directory OpenAPI operation is missing reusable 400/500 error responses: $location"
        }

        Assert-OperationTextMatches `
            $operation `
            "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./(?:Owner|Organization|Repository|Directory)\.Components\.OpenAPI\.yaml#/" `
            "Owner/organization/repository/directory operation must use a family success response component: $location"

        Assert-OperationHasJsonRequestExample `
            $operation `
            "Owner/organization/repository/directory operation with a JSON request body must include an example: $location"

        Assert-OperationSha256ExamplesAreValid $operation @()
    }

    foreach ($expected in $expectedOperations) {
        $operation = Get-RequiredOpenApiOperation $Operations $expected.File $expected.OperationId

        Assert-OperationTextMatches `
            $operation `
            "(?s)tags:\s*-\s+$($expected.Tag)\b" `
            "Operation '$($expected.OperationId)' must carry primary SDK tag '$($expected.Tag)'."

        $componentFilePrefix = $expected.File -replace '\.Paths\.OpenAPI\.yaml$', ''
        Assert-OperationTextMatches `
            $operation `
            "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./$([regex]::Escape($componentFilePrefix))\.Components\.OpenAPI\.yaml#/$($expected.Response)'" `
            "Operation '$($expected.OperationId)' must use '$($expected.Response)' for its 200 envelope."
    }

    foreach ($commandExpectation in @(
            [pscustomobject]@{ File = 'Owner.Components.OpenAPI.yaml'; Schema = 'OwnerCommandReturnValue' },
            [pscustomobject]@{ File = 'Organization.Components.OpenAPI.yaml'; Schema = 'OrganizationCommandReturnValue' },
            [pscustomobject]@{ File = 'Repository.Components.OpenAPI.yaml'; Schema = 'RepositoryCommandReturnValue' },
            [pscustomobject]@{ File = 'Directory.Components.OpenAPI.yaml'; Schema = 'DirectoryCommandReturnValue' }
        )) {
        $componentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot $commandExpectation.File) -Raw
        Assert-OpenApiSchemaPropertyIsString `
            $componentsText `
            $commandExpectation.Schema `
            'ReturnValue' `
            "$($commandExpectation.Schema) must model command success ReturnValue as a string."
    }

    $repositoryComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Repository.Components.OpenAPI.yaml') -Raw
    foreach ($requiredRepositoryContract in @(
            'ObjectStorageProvider:',
            'RepositoryBooleanReturnValue:',
            'RepositoryBranchesReturnValue:',
            'RepositoryReferencesReturnValue:'
        )) {
        Assert-TextContains $repositoryComponentsText $requiredRepositoryContract "Repository OpenAPI components are missing '$requiredRepositoryContract'."
    }

    Assert-OperationTextMatches `
        ([pscustomobject]@{ OperationText = $repositoryComponentsText }) `
        "(?s)RepositoryBooleanReturnValue:\s*.*?ReturnValue:\s*.*?type:\s*boolean" `
        'RepositoryBooleanReturnValue must keep exists/isEmpty ReturnValue as boolean.'

    $directoryComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Directory.Components.OpenAPI.yaml') -Raw
    foreach ($requiredDirectoryContract in @(
            'DirectoryVersionId:',
            'DirectoryVersionApiDto:',
            'DirectoryVersionReturnValue:',
            'DirectoryVersionHashLookupReturnValue:',
            'DirectoryVersionListReturnValue:'
        )) {
        Assert-TextContains $directoryComponentsText $requiredDirectoryContract "Directory OpenAPI components are missing '$requiredDirectoryContract'."
    }

    if ($directoryComponentsText -match '(DirectoryVersionCommand|DirectoryVersionEvent|DirectoryEvent|Actor)') {
        Add-Failure 'Directory OpenAPI components must expose public parameter/DTO shapes, not internal actor command/event shapes.'
    }

    $ownerComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Owner.Components.OpenAPI.yaml') -Raw
    $organizationComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Organization.Components.OpenAPI.yaml') -Raw
    $dtoComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Dto.Components.OpenAPI.yaml') -Raw

    Assert-OpenApiResponseIsRawStringDictionary `
        $ownerComponentsText `
        'OwnerOrganizationsResponse' `
        '/owner/listOrganizations'

    Assert-OpenApiResponseIsRawStringDictionary `
        $organizationComponentsText `
        'OrganizationRepositoriesResponse' `
        '/organization/listRepositories'

    Assert-OpenApiNamedBlockDoesNotContain `
        $ownerComponentsText `
        'OwnerReturnValue' `
        'Organizations' `
        'OwnerReturnValue examples must not include non-existent OwnerDto.Organizations.'

    Assert-OpenApiNamedBlockDoesNotContain `
        $organizationComponentsText `
        'OrganizationReturnValue' `
        'Repositories' `
        'OrganizationReturnValue examples must not include non-existent OrganizationDto.Repositories.'

    Assert-OpenApiNamedBlockDoesNotContain `
        $dtoComponentsText `
        'OwnerDto' `
        'Organizations' `
        'OwnerDto schema must not include non-existent Organizations.'

    Assert-OpenApiNamedBlockDoesNotContain `
        $dtoComponentsText `
        'OrganizationDto' `
        'Repositories' `
        'OrganizationDto schema must not include non-existent Repositories.'

    $getBySha256HashOperation = Get-RequiredOpenApiOperation $Operations 'Directory.Paths.OpenAPI.yaml' 'GetDirectoryVersionBySha256Hash'
    Assert-OperationSha256ExamplesAreValid $getBySha256HashOperation @('Sha256Hash')
    Assert-OperationTextMatches `
        $getBySha256HashOperation `
        "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./Directory\.Components\.OpenAPI\.yaml#/DirectoryVersionSha256HashLookupResponse'" `
        'GetDirectoryVersionBySha256Hash must use the SHA lookup envelope that narrowly models the no-match sentinel.'

    $getByBlake3HashOperation = Get-RequiredOpenApiOperation $Operations 'Directory.Paths.OpenAPI.yaml' 'GetDirectoryVersionByBlake3Hash'
    Assert-OperationTextMatches `
        $getByBlake3HashOperation `
        "(?s)responses:\s*.*?'200':\s*.*?\`$ref:\s*'\./Directory\.Components\.OpenAPI\.yaml#/DirectoryVersionHashLookupResponse'" `
        'GetDirectoryVersionByBlake3Hash must use the strict raw directory-version envelope.'
}

function Test-OpenApiMaterializationContractDetails {
    param([string] $OpenApiRoot)

    $materializationText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Materialization.Components.OpenAPI.yaml') -Raw

    Assert-MaterializationStringEnumSchema $materializationText 'MaterializationExecutionMode' @('direct', 'cachePreferred', 'cacheRequired')
    Assert-MaterializationStringEnumSchema $materializationText 'MaterializationTargetSelectorKind' @(
        'directoryVersionId',
        'referenceId',
        'branchName',
        'referenceType'
    )
    Assert-MaterializationStringEnumSchema $materializationText 'MaterializationCacheSelectionKind' @('bypassCache', 'preferCache', 'requireCache')
    Assert-MaterializationStringEnumSchema $materializationText 'MaterializationArtifactKind' @(
        'directoryVersionZip',
        'recursiveDirectoryMetadata',
        'wholeFileContent',
        'fileManifest',
        'contentBlock'
    )
    Assert-MaterializationStringEnumSchema $materializationText 'MaterializationArtifactSourceKind' @('directUri', 'cacheEntry', 'deferred')

    Assert-OpenApiSchemaRequiresProperties `
        $materializationText `
        'PlanParameters' `
        @('Request') `
        'PlanParameters must require the materialization request while allowing ValidateIdsMiddleware ID-or-name repository authority'

    Assert-TextContains `
        $materializationText `
        'Repository authority accepts the same ID-or-name pairs resolved by ValidateIdsMiddleware' `
        'PlanParameters must document the ID-or-name repository authority contract accepted by the server.'

    $targetSelectorBlock = Get-OpenApiNamedBlock $materializationText 'MaterializationTargetSelector'
    if ($null -eq $targetSelectorBlock) {
        Add-Failure 'MaterializationTargetSelector schema is missing.'
    }
    else {
        $targetSelectorText = [string] $targetSelectorBlock.Text
        foreach ($requiredSelectorNeedle in @(
                'BranchId:',
                'BranchName:',
                'ReferenceType:',
                '$ref: ''Shared.Components.OpenAPI.yaml#/components/schemas/BranchId''',
                '$ref: ''Shared.Components.OpenAPI.yaml#/components/schemas/BranchName''',
                '$ref: ''Shared.Components.OpenAPI.yaml#/components/schemas/ReferenceType''',
                'ReferenceType selectors provide ReferenceType plus exactly one of BranchId or BranchName.'
            )) {
            Assert-TextContains $targetSelectorText $requiredSelectorNeedle "MaterializationTargetSelector schema must publish ReferenceType selector contract member '$requiredSelectorNeedle'."
        }
    }

    foreach ($requiredExampleValue in @(
            'SelectorKind: directoryVersionId',
            'ExecutionMode: direct',
            'SelectionKind: bypassCache',
            'RequestedArtifactKinds: [directoryVersionZip, recursiveDirectoryMetadata]'
        )) {
        Assert-TextContains $materializationText $requiredExampleValue "Materialization OpenAPI examples must use string enum value '$requiredExampleValue'."
    }
}

function Test-OpenApiSharedContractDetails {
    param(
        [string] $OpenApiRoot,
        [object[]] $Operations
    )

    $headersFile = Join-Path $OpenApiRoot 'Transport.Headers.OpenAPI.yaml'
    if (-not (Test-Path -LiteralPath $headersFile -PathType Leaf)) {
        Add-Failure 'Reusable transport header components are not represented yet; no header contract is accepted.'
        return
    }

    $headersText = Get-Content -LiteralPath $headersFile -Raw
    foreach ($requiredHeader in @(
            'name: X-Correlation-Id',
            'name: X-Api-Version',
            'name: X-Grace-Client-Type',
            'name: X-Grace-Client-Version',
            'UploadSessionLifecycleState:',
            'name: x-grace-webhook-delivery-id',
            'name: x-grace-webhook-event-name',
            'name: x-grace-webhook-event-version',
            'name: x-grace-webhook-timestamp',
            'name: x-grace-webhook-signature-key-id',
            'name: x-grace-webhook-payload-sha256',
            'name: x-grace-webhook-signature',
            'not authentication proof'
        )) {
        Assert-TextContains $headersText $requiredHeader "Reusable header contract is missing '$requiredHeader'."
    }

    if ($headersText -match '(?m)^\s+Webhook(?:DeliveryId|EventName|EventVersion|Timestamp|SignatureKeyId|PayloadSha256|Signature):\s*$') {
        Add-Failure 'Outbound webhook headers must not be represented only as components.headers aliases without exact wire names.'
    }

    $sharedText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Shared.Components.OpenAPI.yaml') -Raw
    foreach ($requiredHashContract in @(
            [pscustomobject]@{ Schema = 'FileVersion'; Property = 'Sha256Hash' },
            [pscustomobject]@{ Schema = 'FileVersion'; Property = 'Blake3Hash' },
            [pscustomobject]@{ Schema = 'DirectoryVersion'; Property = 'DirectoryVersionId' },
            [pscustomobject]@{ Schema = 'DirectoryVersion'; Property = 'Sha256Hash' },
            [pscustomobject]@{ Schema = 'DirectoryVersion'; Property = 'Blake3Hash' },
            [pscustomobject]@{ Schema = 'DirectoryVersion'; Property = 'HashesValidated' }
        )) {
        Assert-OpenApiSchemaHasProperty `
            $sharedText `
            $requiredHashContract.Schema `
            $requiredHashContract.Property `
            "$($requiredHashContract.Schema) must expose $($requiredHashContract.Property) in the public OpenAPI schema."
    }

    Assert-OperationTextMatches `
        ([pscustomobject]@{ OperationText = $sharedText }) `
        "(?s)DirectoryVersion:\s*.*?Sha256Hash:\s*.*?\`$ref:\s*'#/components/schemas/Sha256Hash'" `
        'Persisted DirectoryVersion.Sha256Hash must stay a strict full SHA-256 hash.'

    $directoryComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Directory.Components.OpenAPI.yaml') -Raw
    Assert-OperationTextMatches `
        ([pscustomobject]@{ OperationText = $directoryComponentsText }) `
        "(?s)DirectoryVersionHashLookupResult:\s*.*?Sha256Hash:\s*.*?\`$ref:\s*'Shared\.Components\.OpenAPI\.yaml#/components/schemas/Sha256HashLookupSentinelCompatibility'" `
        'Only the directory hash lookup response may model the DirectoryVersion.Default empty SHA-256 sentinel.'

    Assert-OperationTextMatches `
        ([pscustomobject]@{ OperationText = $directoryComponentsText }) `
        "(?s)DirectoryVersionSha256HashLookupReturnValue:\s*.*?ReturnValue:\s*.*?\`$ref:\s*'#/DirectoryVersionHashLookupResult'" `
        'Only SHA directory hash lookup responses may use the sentinel-aware result schema rather than weakening persisted DirectoryVersion.'

    Assert-OperationTextMatches `
        ([pscustomobject]@{ OperationText = $directoryComponentsText }) `
        "(?s)DirectoryVersionHashLookupReturnValue:\s*.*?ReturnValue:\s*.*?\`$ref:\s*'Shared\.Components\.OpenAPI\.yaml#/components/schemas/DirectoryVersion'" `
        'BLAKE3 directory hash lookup responses must keep the strict persisted DirectoryVersion schema.'

    foreach ($requiredGraceErrorPart in @(
            'Grace domain error envelope',
            'Authentication challenges and framework authorization',
            'required:',
            '- Error',
            '- EventTime',
            '- CorrelationId',
            '- Properties'
        )) {
        Assert-TextContains $sharedText $requiredGraceErrorPart "GraceError contract is missing '$requiredGraceErrorPart'."
    }

    $directoryPathsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Directory.Paths.OpenAPI.yaml') -Raw
    Assert-TextContains $directoryPathsText 'Blake3Hash: 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d' `
        'Directory create/save examples must show BLAKE3 alongside SHA-256.'

    Assert-TextContains $sharedText 'distinct from the X-Correlation-Id transport header' 'Body CorrelationId must be documented as distinct from the transport header.'

    $responsesText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Responses.OpenAPI.yaml') -Raw
    Assert-TextContains $responsesText "Shared.Components.OpenAPI.yaml#/components/schemas/GraceError" 'Reusable 400/500 responses must reference GraceError.'
    Assert-TextContains $responsesText "'401':" 'Reusable 401 authentication response must be present.'
    Assert-TextContains $responsesText 'WWW-Authenticate:' 'Reusable 401 response must document the auth challenge header.'
    Assert-TextContains $responsesText "'403':" 'Reusable 403 authorization response must be present.'
    Assert-TextContains $responsesText 'value: Forbidden.' 'Reusable 403 response must stay distinct from GraceError.'

    foreach ($operationId in @('GetDownloadUri', 'GetContentBlockUploadUri', 'GetContentBlockDownloadUri')) {
        $operation = Get-RequiredOpenApiOperation $Operations 'Storage.Paths.OpenAPI.yaml' $operationId
        Assert-OperationTextMatches `
            $operation `
            "(?s)responses:\s*.*?'200':\s*.*?content:\s*.*?text/plain:" `
            "Storage raw URI operation '$operationId' must keep its own 200 response as text/plain."
    }

    foreach ($operationId in @('CreateApprovalPolicy', 'ApproveApprovalRequest')) {
        $operation = Get-RequiredOpenApiOperation $Operations 'Approval.Paths.OpenAPI.yaml' $operationId
        Assert-OperationTextMatches `
            $operation `
            "(?s)requestBody:\s*.*?content:\s*.*?application/json:\s*.*?examples:" `
            "Approval operation '$operationId' must include an application/json example in its own request body."
    }

    foreach ($operationId in @('CreateWebhookRule', 'TestWebhookRule')) {
        $operation = Get-RequiredOpenApiOperation $Operations 'Webhook.Paths.OpenAPI.yaml' $operationId
        Assert-OperationTextMatches `
            $operation `
            "(?s)requestBody:\s*.*?content:\s*.*?application/json:\s*.*?examples:" `
            "Webhook operation '$operationId' must include an application/json example in its own request body."
    }

    Test-OpenApiBranchReferenceDiffDetails $OpenApiRoot $Operations
    Test-OpenApiOwnerOrganizationRepositoryDirectoryDetails $OpenApiRoot $Operations
    Test-OpenApiMaterializationContractDetails $OpenApiRoot
}

function Test-OpenApiQuality {
    param([string] $RepoRoot)

    Write-Host '== OpenAPI quality =='
    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $operations = Get-OpenApiOperations $openApiRoot
    $methodDeclarationCount = Get-OpenApiMethodDeclarationCount $openApiRoot

    if ($operations.Count -eq 0) {
        Add-Failure 'No OpenAPI operations were found.'
        return
    }

    if ($operations.Count -ne $methodDeclarationCount) {
        Add-Failure "OpenAPI operation parser scanned $($operations.Count) operations but found $methodDeclarationCount represented HTTP method declarations."
    }

    $missingOperationId = @($operations | Where-Object { [string]::IsNullOrWhiteSpace($_.OperationId) })
    if ($missingOperationId.Count -gt 0) {
        $examples = ($missingOperationId | Select-Object -First 10 | ForEach-Object {
            "$($_.File):$($_.LineNumber) $($_.Method.ToUpperInvariant()) $($_.Path)"
        }) -join '; '
        Add-Pending "OperationId coverage is not yet accepted. Missing operationId values: $($missingOperationId.Count). Examples: $examples"
    }

    $duplicates = @($operations |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.OperationId) } |
        Group-Object OperationId |
        Where-Object { $_.Count -gt 1 })

    foreach ($duplicate in $duplicates) {
        $locations = ($duplicate.Group | ForEach-Object { "$($_.File):$($_.LineNumber)" }) -join ', '
        Add-Failure "Duplicate operationId '$($duplicate.Name)': $locations"
    }

    $missingTags = @($operations | Where-Object { -not $_.HasTag })
    if ($missingTags.Count -gt 0) {
        $examples = ($missingTags | Select-Object -First 10 | ForEach-Object {
            "$($_.File):$($_.LineNumber) $($_.Method.ToUpperInvariant()) $($_.Path)"
        }) -join '; '
        Add-Pending "Operation tag coverage is not yet accepted. Missing tags: $($missingTags.Count). Examples: $examples"
    }

    $missingResponses = @($operations | Where-Object { -not $_.HasResponses })
    foreach ($operation in $missingResponses) {
        Add-Failure "Missing responses block: $($operation.File):$($operation.LineNumber) $($operation.Method.ToUpperInvariant()) $($operation.Path)"
    }

    $missingErrorResponses = @($operations | Where-Object { -not ($_.Has400 -and $_.Has500) })
    if ($missingErrorResponses.Count -gt 0) {
        $examples = ($missingErrorResponses | Select-Object -First 10 | ForEach-Object {
            "$($_.File):$($_.LineNumber) $($_.Method.ToUpperInvariant()) $($_.Path)"
        }) -join '; '
        Add-Pending "Error response coverage is not yet accepted. Missing 400/500 pairs: $($missingErrorResponses.Count). Examples: $examples"
    }

    Test-OpenApiSharedContractDetails $openApiRoot $operations

    Add-Pass "Scanned $($operations.Count) OpenAPI operations for operationId, tag, response, and header proof scaffolding."
}

function Test-SdkPackageProof {
    param([string] $RepoRoot)

    Write-Host '== SDK package export/import proof =='
    $packageProofPath = Join-Path $RepoRoot 'sdk/package-proof.json'
    if (-not (Test-Path -LiteralPath $packageProofPath -PathType Leaf)) {
        Add-Pending 'No stable SDK package export/import proof exists; raw generated-client matrix acceptance remains limited to guardrailed OpenAPI Generator proof artifacts behind facades.'
        return
    }

    Add-Pending "SDK package export/import proof is not accepted yet. A future verifier must validate package metadata, exported facade surface, import execution evidence, and generator isolation before this gate can pass. Placeholder path: $packageProofPath"
}

function Get-RequiredJsonProperty {
    param(
        [object] $Object,
        [string] $PropertyName,
        [string] $Context
    )

    if ($null -eq $Object) {
        Add-Failure "$Context is missing; expected property '$PropertyName'."
        return $null
    }

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        Add-Failure "$Context is missing required property '$PropertyName'."
        return $null
    }

    return $property.Value
}

function Get-ProjectionHashFromManifest {
    param(
        [string] $RepoRoot,
        [string] $ProjectionRelativePath
    )

    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $manifest = Get-OpenApiManifest $openApiRoot
    if ($null -eq $manifest) {
        return $null
    }

    $projectionArtifact =
        @($manifest.generatedArtifacts) |
        Where-Object { $_.path -eq ([IO.Path]::GetFileName($ProjectionRelativePath)) } |
        Select-Object -First 1

    if ($null -eq $projectionArtifact) {
        Add-Failure "OpenAPI proof manifest is missing generated artifact '$ProjectionRelativePath'."
        return $null
    }

    $manifestHash = [string] (Get-RequiredJsonProperty $projectionArtifact 'sha256' "OpenAPI proof manifest artifact '$ProjectionRelativePath'")
    if ([string]::IsNullOrWhiteSpace($manifestHash)) {
        Add-Failure "OpenAPI proof manifest artifact '$ProjectionRelativePath' is missing sha256."
        return $null
    }

    return $manifestHash.ToLowerInvariant()
}

function Test-SdkGeneratedHarnessFreshness {
    param(
        [string] $RepoRoot,
        [string] $ProjectionRelativePath,
        [string] $ActualProjectionHash,
        [string] $ManifestProjectionHash
    )

    $reportPath = Join-Path $RepoRoot 'sdk/generated/generator-report.json'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        Add-Failure "Missing SDK generator report: $reportPath"
        return
    }

    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $reportedProjection = [string] (Get-RequiredJsonProperty $report 'sourceProjection' 'SDK generator report')
    $reportedProjectionHash = [string] (Get-RequiredJsonProperty $report 'sourceProjectionSha256' 'SDK generator report')

    if ($reportedProjection -ne $ProjectionRelativePath) {
        Add-Failure "SDK generator report sourceProjection must be '$ProjectionRelativePath'; actual '$reportedProjection'."
    }

    if ($reportedProjectionHash.ToLowerInvariant() -ne $ActualProjectionHash) {
        Add-Failure "SDK generator report is stale for $ProjectionRelativePath. Expected final projection hash $ActualProjectionHash, actual $reportedProjectionHash."
    }

    if ($null -ne $ManifestProjectionHash -and $reportedProjectionHash.ToLowerInvariant() -ne $ManifestProjectionHash) {
        Add-Failure "SDK generator report hash must match OpenAPI proof manifest for $ProjectionRelativePath. Manifest $ManifestProjectionHash, report $reportedProjectionHash."
    }

    foreach ($generatedFile in @($report.generatedFiles)) {
        $kind = [string] $generatedFile.kind
        if (-not $kind.EndsWith('-generated-metadata', [StringComparison]::Ordinal)) {
            continue
        }

        $relativePath = [string] (Get-RequiredJsonProperty $generatedFile 'path' 'SDK generator report generated file')
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        $metadataPath = Join-Path $RepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            Add-Failure "SDK generated metadata file is missing: $relativePath"
            continue
        }

        $metadataText = Get-Content -LiteralPath $metadataPath -Raw
        Assert-TextContains $metadataText $ProjectionRelativePath "SDK generated metadata file $relativePath must name source projection '$ProjectionRelativePath'."
        Assert-TextContains $metadataText $ActualProjectionHash "SDK generated metadata file $relativePath must carry final projection hash '$ActualProjectionHash'."

        if ($null -ne $ManifestProjectionHash -and -not $metadataText.Contains($ManifestProjectionHash, [StringComparison]::Ordinal)) {
            Add-Failure "SDK generated metadata file $relativePath must match OpenAPI proof manifest projection hash '$ManifestProjectionHash'."
        }
    }

    Add-Pass 'SDK generator report and generated language metadata match the final OpenAPI projection and proof manifest hash.'
}

function Test-GeneratedClientMatrixProof {
    param([string] $RepoRoot)

    Write-Host '== Generated-client matrix proof =='
    $matrixPath = Join-Path $RepoRoot 'sdk/generated/matrix/generator-matrix-evidence.json'
    if (-not (Test-Path -LiteralPath $matrixPath -PathType Leaf)) {
        Add-Failure "Missing generated-client matrix evidence: $matrixPath"
        return
    }

    $evidence = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
    $projectionRelativePath = Get-RequiredJsonProperty $evidence 'sourceProjection' 'Generator matrix evidence'
    if ([string]::IsNullOrWhiteSpace([string] $projectionRelativePath)) {
        Add-Failure 'Generator matrix evidence is missing sourceProjection.'
        return
    }

    $projectionPath = Join-Path $RepoRoot ([string] $projectionRelativePath)
    $actualProjectionHash = $null
    if (-not (Test-Path -LiteralPath $projectionPath -PathType Leaf)) {
        Add-Failure "Generator matrix source projection is missing: $projectionRelativePath"
    }
    else {
        $actualProjectionHash = Get-FileSha256 $projectionPath
        $recordedProjectionHash = [string] (Get-RequiredJsonProperty $evidence 'sourceProjectionSha256' 'Generator matrix evidence')
        if ($actualProjectionHash -ne $recordedProjectionHash.ToLowerInvariant()) {
            Add-Failure "Generator matrix evidence is stale for $projectionRelativePath. Expected $recordedProjectionHash, actual $actualProjectionHash."
        }
    }

    $manifestProjectionHash = Get-ProjectionHashFromManifest $RepoRoot ([string] $projectionRelativePath)
    if ($null -ne $actualProjectionHash -and $null -ne $manifestProjectionHash -and $actualProjectionHash -ne $manifestProjectionHash) {
        Add-Failure "OpenAPI proof manifest is stale for $projectionRelativePath. Manifest $manifestProjectionHash, actual $actualProjectionHash."
    }

    if ($null -ne $actualProjectionHash) {
        Test-SdkGeneratedHarnessFreshness $RepoRoot ([string] $projectionRelativePath) $actualProjectionHash $manifestProjectionHash
    }

    $acceptedTier = [string] (Get-RequiredJsonProperty $evidence 'acceptedTier' 'Generator matrix evidence')
    if ($acceptedTier -ne 'raw-openapi-generator-behind-facades') {
        Add-Failure "Generator matrix acceptedTier must be 'raw-openapi-generator-behind-facades'; actual '$acceptedTier'."
    }

    $sdkGrade = Get-RequiredJsonProperty $evidence 'sdkGradeGeneratedClientAccepted' 'Generator matrix evidence'
    if ($sdkGrade -ne $false) {
        Add-Failure 'Generator matrix evidence must not claim SDK-grade generated-client acceptance while strict generator validation remains rejected.'
    }

    $pendingRationale = [string] (Get-RequiredJsonProperty $evidence 'pendingSdkGradeRationale' 'Generator matrix evidence')
    if ([string]::IsNullOrWhiteSpace($pendingRationale)) {
        Add-Failure 'Generator matrix evidence must record why SDK-grade generated-client acceptance remains pending.'
    }

    if ($null -eq $evidence.toolVersions -or $null -eq $evidence.toolVersions.PSObject.Properties['kiota']) {
        Add-Failure 'Generator matrix evidence must record an executed Kiota version probe.'
    }
    else {
        $kiotaVersion = [string] $evidence.toolVersions.kiota
        if ([string]::IsNullOrWhiteSpace($kiotaVersion) -or
            $kiotaVersion -eq 'unavailable' -or
            $kiotaVersion -match 'does not exist|not recognized|failed|unavailable') {
            Add-Failure "Generator matrix Kiota version proof must come from an executable Kiota tool; actual '$kiotaVersion'."
        }
    }

    foreach ($traceId in @('B-043', 'B-044', 'drift-002', 'drift-006', 'drift-008')) {
        if ($null -eq $evidence.traceability -or $null -eq $evidence.traceability.PSObject.Properties[$traceId]) {
            Add-Failure "Generator matrix traceability is missing '$traceId'."
        }
    }

    $matrixEntries = @($evidence.matrix)
    $acceptedLanguages = @('TypeScript', 'Python', 'Rust')
    foreach ($language in $acceptedLanguages) {
        $entry = $matrixEntries |
            Where-Object { $_.generator -eq 'OpenAPI Generator' -and $_.language -eq $language } |
            Select-Object -First 1

        if ($null -eq $entry) {
            Add-Failure "Generator matrix is missing OpenAPI Generator $language entry."
            continue
        }

        if ($entry.outcome -ne 'Accepted with guardrails' -or $entry.exitCode -ne 0) {
            Add-Failure "OpenAPI Generator $language must be accepted with guardrails and exit code 0; actual outcome '$($entry.outcome)' exitCode '$($entry.exitCode)'."
        }

        if ([string]::IsNullOrWhiteSpace([string] $entry.outputPath)) {
            Add-Failure "OpenAPI Generator $language entry is missing outputPath."
        }
        else {
            $outputPath = Join-Path $RepoRoot ([string] $entry.outputPath)
            if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
                Add-Failure "OpenAPI Generator $language output path is missing: $($entry.outputPath)"
            }
        }
    }

    foreach ($rejected in @(
            @{ generator = 'Kiota'; language = '.NET' },
            @{ generator = 'Kiota'; language = 'TypeScript' },
            @{ generator = 'NSwag'; language = '.NET' }
        )) {
        $entry = $matrixEntries |
            Where-Object { $_.generator -eq $rejected.generator -and $_.language -eq $rejected.language } |
            Select-Object -First 1

        if ($null -eq $entry) {
            Add-Failure "Generator matrix is missing $($rejected.generator) $($rejected.language) rejection evidence."
        }
        elseif ($entry.outcome -ne 'Rejected') {
            Add-Failure "$($rejected.generator) $($rejected.language) must remain rejected until schema-shape debt is fixed; actual outcome '$($entry.outcome)'."
        }
        elseif ($rejected.generator -eq 'Kiota') {
            $kiotaEvidence = @($entry.evidence | ForEach-Object { [string] $_ })
            $kiotaEvidenceText = $kiotaEvidence -join "`n"
            if ($kiotaEvidence.Count -eq 0) {
                Add-Failure "Generator matrix $($rejected.generator) $($rejected.language) rejection must include Kiota command output evidence."
            }

            if ($kiotaEvidenceText -match 'application to execute does not exist|does not exist: .*kiota|missing executable' -or
                [string] $entry.conclusion -match 'missing executable|tool.*missing|tool.*unavailable') {
                Add-Failure "Generator matrix $($rejected.generator) $($rejected.language) rejection records tool infrastructure failure instead of executed schema-shape evidence."
            }
        }
    }

    foreach ($probeName in @(
            'TypeScript generated client import/build',
            'Python generated client import/build',
            'Rust generated client cargo check'
        )) {
        $probe = @($evidence.probes) | Where-Object { $_.name -eq $probeName } | Select-Object -First 1
        if ($null -eq $probe) {
            Add-Failure "Generator matrix is missing required probe '$probeName'."
        }
        elseif ($probe.exitCode -ne 0) {
            Add-Failure "Generator matrix probe '$probeName' failed with exit code $($probe.exitCode)."
        }
    }

    $generatedEnumChecks = @(
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/MaterializationExecutionMode.ts'
            required = @("Direct: 'direct'", "CachePreferred: 'cachePreferred'", "CacheRequired: 'cacheRequired'")
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/MaterializationTargetSelectorKind.ts'
            required = @("DirectoryVersionId: 'directoryVersionId'", "ReferenceId: 'referenceId'", "BranchName: 'branchName'", "ReferenceType: 'referenceType'")
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3', 'NUMBER_4')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/ReferenceType.ts'
            required = @("Promotion: 'promotion'", "Commit: 'commit'", "Checkpoint: 'checkpoint'", "Save: 'save'", "Tag: 'tag'", "External: 'external'", "Rebase: 'rebase'")
            forbidden = @("Promotion: 'Promotion'", "Commit: 'Commit'", "Checkpoint: 'Checkpoint'", "Save: 'Save'", "Tag: 'Tag'", "External: 'External'", "Rebase: 'Rebase'", 'NUMBER_1', 'NUMBER_2', 'NUMBER_3', 'NUMBER_4', 'NUMBER_5', 'NUMBER_6', 'NUMBER_7')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/materialization_target_selector_kind.py'
            required = @('directoryVersionId', 'referenceId', 'branchName', 'referenceType')
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3', 'NUMBER_4')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/reference_type.py'
            required = @("PROMOTION = 'promotion'", "COMMIT = 'commit'", "CHECKPOINT = 'checkpoint'", "SAVE = 'save'", "TAG = 'tag'", "EXTERNAL = 'external'", "REBASE = 'rebase'")
            forbidden = @("PROMOTION = 'Promotion'", "COMMIT = 'Commit'", "CHECKPOINT = 'Checkpoint'", "SAVE = 'Save'", "TAG = 'Tag'", "EXTERNAL = 'External'", "REBASE = 'Rebase'", 'NUMBER_1', 'NUMBER_2', 'NUMBER_3', 'NUMBER_4', 'NUMBER_5', 'NUMBER_6', 'NUMBER_7')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/materialization_target_selector_kind.rs'
            required = @('directoryVersionId', 'referenceId', 'branchName', 'referenceType')
            forbidden = @('Number1', 'Number2', 'Number3', 'Number4')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/reference_type.rs'
            required = @('#[serde(rename = "promotion")]', '#[serde(rename = "commit")]', '#[serde(rename = "checkpoint")]', '#[serde(rename = "save")]', '#[serde(rename = "tag")]', '#[serde(rename = "external")]', '#[serde(rename = "rebase")]')
            forbidden = @('#[serde(rename = "Promotion")]', '#[serde(rename = "Commit")]', '#[serde(rename = "Checkpoint")]', '#[serde(rename = "Save")]', '#[serde(rename = "Tag")]', '#[serde(rename = "External")]', '#[serde(rename = "Rebase")]', 'Number1', 'Number2', 'Number3', 'Number4', 'Number5', 'Number6', 'Number7')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/MaterializationCacheSelectionKind.ts'
            required = @("BypassCache: 'bypassCache'", "PreferCache: 'preferCache'", "RequireCache: 'requireCache'")
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/MaterializationArtifactKind.ts'
            required = @("DirectoryVersionZip: 'directoryVersionZip'", "RecursiveDirectoryMetadata: 'recursiveDirectoryMetadata'", "WholeFileContent: 'wholeFileContent'")
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3', 'NUMBER_4', 'NUMBER_5')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/MaterializationArtifactSourceKind.ts'
            required = @("DirectUri: 'directUri'", "CacheEntry: 'cacheEntry'", "Deferred: 'deferred'")
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/materialization_execution_mode.py'
            required = @('direct', 'cachePreferred', 'cacheRequired')
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/materialization_artifact_kind.py'
            required = @('directoryVersionZip', 'recursiveDirectoryMetadata', 'wholeFileContent')
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3', 'NUMBER_4', 'NUMBER_5')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/materialization_artifact_source_kind.py'
            required = @('directUri', 'cacheEntry', 'deferred')
            forbidden = @('NUMBER_1', 'NUMBER_2', 'NUMBER_3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/materialization_execution_mode.rs'
            required = @('direct', 'cachePreferred', 'cacheRequired')
            forbidden = @('Number1', 'Number2', 'Number3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/materialization_artifact_kind.rs'
            required = @('directoryVersionZip', 'recursiveDirectoryMetadata', 'wholeFileContent')
            forbidden = @('Number1', 'Number2', 'Number3', 'Number4', 'Number5')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/materialization_artifact_source_kind.rs'
            required = @('directUri', 'cacheEntry', 'deferred')
            forbidden = @('Number1', 'Number2', 'Number3')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/PlanParameters.ts'
            required = @('ownerId?: string;', 'ownerName?: string;', 'organizationId?: string;', 'organizationName?: string;', 'repositoryId?: string;', 'repositoryName?: string;', "if (!('request' in value)")
            forbidden = @('ownerId: string;', 'organizationId: string;', 'repositoryId: string;', "if (!('ownerId' in value)", "if (!('organizationId' in value)", "if (!('repositoryId' in value)")
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/MaterializationTargetSelector.ts'
            required = @('branchId?: string;', 'branchName?: string;', 'referenceType?: ReferenceType;', "'BranchId': value['branchId']", "'ReferenceType': ReferenceTypeToJSON(value['referenceType'])")
            forbidden = @('branchId: string;', 'referenceType: ReferenceType;')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/materialization_target_selector.py'
            required = @('branch_id: Optional[UUID]', 'branch_name: Optional[StrictStr]', 'reference_type: Optional[ReferenceType]', '"BranchId"', '"BranchName"', '"ReferenceType"')
            forbidden = @('branch_id: UUID', 'reference_type: ReferenceType = Field(alias="ReferenceType")')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/materialization_target_selector.rs'
            required = @('pub branch_id: Option<uuid::Uuid>', 'pub branch_name: Option<String>', 'pub reference_type: Option<models::ReferenceType>')
            forbidden = @('pub branch_id: uuid::Uuid', 'pub reference_type: models::ReferenceType')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/plan_parameters.py'
            required = @('owner_id: Optional[UUID]', 'owner_name: Optional[StrictStr]', 'organization_id: Optional[UUID]', 'organization_name: Optional[StrictStr]', 'repository_id: Optional[UUID]', 'repository_name: Optional[StrictStr]', 'request: MaterializationPlanRequest')
            forbidden = @('owner_id: UUID', 'organization_id: UUID', 'repository_id: UUID')
        },
        @{
            path = 'sdk/generated/matrix/openapi-generator/rust/src/models/plan_parameters.rs'
            required = @('pub owner_id: Option<uuid::Uuid>', 'pub owner_name: Option<String>', 'pub organization_id: Option<uuid::Uuid>', 'pub organization_name: Option<String>', 'pub repository_id: Option<uuid::Uuid>', 'pub repository_name: Option<String>', 'pub request: Box<models::MaterializationPlanRequest>', 'pub fn new(request: models::MaterializationPlanRequest)')
            forbidden = @('pub owner_id: uuid::Uuid', 'pub organization_id: uuid::Uuid', 'pub repository_id: uuid::Uuid', 'pub fn new(owner_id: uuid::Uuid')
        }
    )

    foreach ($check in $generatedEnumChecks) {
        $generatedPath = Join-Path $RepoRoot ([string] $check.path)
        if (-not (Test-Path -LiteralPath $generatedPath -PathType Leaf)) {
            Add-Failure "Generated-client contract proof file is missing: $($check.path)"
            continue
        }

        $generatedText = Get-Content -LiteralPath $generatedPath -Raw
        foreach ($requiredNeedle in @($check.required)) {
            Assert-TextContains $generatedText $requiredNeedle "Generated-client contract proof $($check.path) is missing '$requiredNeedle'."
        }

        foreach ($forbiddenNeedle in @($check.forbidden)) {
            if ($generatedText.Contains($forbiddenNeedle, [StringComparison]::Ordinal)) {
                Add-Failure "Generated-client contract proof $($check.path) must not contain stale numeric enum or optional authority shape '$forbiddenNeedle'."
            }
        }
    }

    foreach ($entry in @($evidence.deterministicRegeneration)) {
        $path = Join-Path $RepoRoot ([string] $entry.path)
        $actualHash = Get-DirectoryManifestHash $path
        if ($actualHash -ne ([string] $entry.manifestSha256).ToLowerInvariant()) {
            Add-Failure "Generator matrix deterministic hash is stale for $($entry.path). Expected $($entry.manifestSha256), actual $actualHash."
        }
    }

    Add-Pass 'Generated-client matrix accepts OpenAPI Generator TypeScript, Python, and Rust raw-client proof points with guardrails; Kiota executed and remains explicitly rejected with schema-shape evidence.'
}

function Test-ProtocolVectorProof {
    param([string] $RepoRoot)

    Write-Host '== Protocol vector proof =='
    $vectorRoot = Join-Path $RepoRoot 'test-vectors/protocol'
    if (-not (Test-Path -LiteralPath $vectorRoot -PathType Container)) {
        Add-Pending 'No protocol vector suite exists; no Tier 3 or Tier 4 protocol parity is accepted by this slice.'
        return
    }

    Add-Pending "Protocol vector proof is not accepted yet. A future verifier must validate vector schema, required coverage, and parity execution results before this gate can pass. Placeholder root: $vectorRoot"
}

$repoRoot = Get-RepoRoot

switch ($Check) {
    'All' {
        Test-OpenApiFreshness $repoRoot
        Test-OpenApiCanonicalVersion $repoRoot
        Test-OpenApiProjections $repoRoot
        Test-OpenApiQuality $repoRoot
        Test-GeneratedClientMatrixProof $repoRoot
        Test-SdkPackageProof $repoRoot
        Test-ProtocolVectorProof $repoRoot
    }
    'Freshness' { Test-OpenApiFreshness $repoRoot }
    'CanonicalVersion' { Test-OpenApiCanonicalVersion $repoRoot }
    'Projections' { Test-OpenApiProjections $repoRoot }
    'Quality' { Test-OpenApiQuality $repoRoot }
    'GeneratedClientMatrix' { Test-GeneratedClientMatrixProof $repoRoot }
    'SdkPackage' { Test-SdkPackageProof $repoRoot }
    'ProtocolVectors' { Test-ProtocolVectorProof $repoRoot }
}

if ($script:Failures.Count -gt 0) {
    Write-Error "OpenAPI proof checks failed: $($script:Failures.Count) failure(s), $($script:Pending.Count) pending gate(s)."
    exit 1
}

if ($script:Pending.Count -gt 0) {
    Write-Host "OpenAPI proof checks completed with $($script:Pending.Count) pending gate(s)."
}
else {
    Write-Host 'OpenAPI proof checks completed with no pending gates.'
}
