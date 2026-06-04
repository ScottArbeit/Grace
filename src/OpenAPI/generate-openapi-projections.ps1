[CmdletBinding()]
param(
    [string] $RedoclyVersion = '2.31.6'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Write-Utf8NoBomLfFile {
    param(
        [string] $Path,
        [string] $Text
    )

    $normalizedText = $Text -replace "`r`n", "`n"
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $normalizedText, $encoding)
}

function Get-CanonicalSourceFiles {
    param([string] $OpenApiRoot)

    Get-ChildItem -LiteralPath $OpenApiRoot -Filter '*.OpenAPI.yaml' -File |
        Where-Object { $_.Name -notin @('Grace.OpenAPI.yaml', 'Grace.OpenAPI.3.1.2.yaml') } |
        Sort-Object Name |
        ForEach-Object {
            [pscustomobject]@{
                path = $_.Name
                sha256 = Get-FileSha256 $_.FullName
            }
        }
}

function Get-CanonicalSourceDigest {
    param([object[]] $CanonicalSourceFiles)

    $digestInput = ($CanonicalSourceFiles |
        Sort-Object path |
        ForEach-Object { "$($_.path):$($_.sha256)" }) -join "`n"

    return Get-StringSha256 $digestInput
}

function Assert-OpenApiVersion {
    param(
        [string] $Path,
        [string] $ExpectedVersion
    )

    $firstLine = (Get-Content -LiteralPath $Path -TotalCount 1)
    if ($firstLine -ne "openapi: $ExpectedVersion") {
        throw "Expected $Path to start with 'openapi: $ExpectedVersion', but found '$firstLine'."
    }
}

$repoRoot = Get-RepoRoot
$openApiRoot = Get-OpenApiRoot $repoRoot
$mainPath = Join-Path $openApiRoot 'Main.OpenAPI.yaml'
$bundlePath = Join-Path $openApiRoot 'Grace.OpenAPI.yaml'
$projectionPath = Join-Path $openApiRoot 'Grace.OpenAPI.3.1.2.yaml'
$lossReportPath = Join-Path $openApiRoot 'Grace.OpenAPI.3.1.2.loss-report.json'
$manifestPath = Join-Path $openApiRoot 'OpenAPI.ProofManifest.json'

Assert-OpenApiVersion $mainPath '3.2.0'

Write-Host "Bundling canonical OpenAPI 3.2 source with @redocly/cli@$RedoclyVersion..."
npx --yes "@redocly/cli@$RedoclyVersion" bundle $mainPath --output $bundlePath
if ($LASTEXITCODE -ne 0) {
    throw "Redocly bundle failed with exit code $LASTEXITCODE."
}

Assert-OpenApiVersion $bundlePath '3.2.0'

$bundleText = Get-Content -LiteralPath $bundlePath -Raw
$projectionText = $bundleText -replace '\Aopenapi:\s*3\.2\.0', 'openapi: 3.1.2'
Write-Utf8NoBomLfFile $projectionPath $projectionText
Assert-OpenApiVersion $projectionPath '3.1.2'

$lossReport = [ordered]@{
    schemaVersion = 1
    sourceArtifact = 'Grace.OpenAPI.yaml'
    projectionArtifact = 'Grace.OpenAPI.3.1.2.yaml'
    projectionTarget = 'openapi-generator-compatibility'
    projectionOpenApiVersion = '3.1.2'
    generatorTool = '@redocly/cli'
    generatorToolVersion = $RedoclyVersion
    transformations = @(
        [ordered]@{
            kind = 'openapi-version-downgrade'
            from = '3.2.0'
            to = '3.1.2'
            lossCategory = 'compatibility-metadata'
            description = 'The generator projection declares OpenAPI 3.1.2 because some SDK generators lag canonical OpenAPI 3.2.0 support.'
        }
    )
    lostSemantics = @(
        'The projection does not advertise that the canonical Grace API contract is OpenAPI 3.2.0.'
    )
    nonLossChanges = @(
        'All content other than the root openapi version line is copied from the freshly bundled canonical artifact.'
    )
}

$lossReportJson = $lossReport | ConvertTo-Json -Depth 10
Write-Utf8NoBomLfFile $lossReportPath "$lossReportJson`n"

$canonicalSourceFiles = @(Get-CanonicalSourceFiles $openApiRoot)
$canonicalSourceDigest = Get-CanonicalSourceDigest $canonicalSourceFiles
$generatedArtifacts = @(
    [ordered]@{
        path = 'Grace.OpenAPI.yaml'
        kind = 'canonical-bundle'
        sourceDigest = $canonicalSourceDigest
        generator = '@redocly/cli'
        generatorVersion = $RedoclyVersion
        command = 'pwsh ./src/OpenAPI/generate-openapi-projections.ps1'
        sha256 = Get-FileSha256 $bundlePath
    },
    [ordered]@{
        path = 'Grace.OpenAPI.3.1.2.yaml'
        kind = 'generator-projection'
        sourceDigest = $canonicalSourceDigest
        generator = '@redocly/cli'
        generatorVersion = $RedoclyVersion
        command = 'pwsh ./src/OpenAPI/generate-openapi-projections.ps1'
        lossReport = 'Grace.OpenAPI.3.1.2.loss-report.json'
        sha256 = Get-FileSha256 $projectionPath
    },
    [ordered]@{
        path = 'Grace.OpenAPI.3.1.2.loss-report.json'
        kind = 'projection-loss-report'
        sourceDigest = $canonicalSourceDigest
        generator = '@redocly/cli'
        generatorVersion = $RedoclyVersion
        command = 'pwsh ./src/OpenAPI/generate-openapi-projections.ps1'
        sha256 = Get-FileSha256 $lossReportPath
    }
)

$manifest = [ordered]@{
    schemaVersion = 2
    canonicalSourceRoot = 'src/OpenAPI'
    canonicalOpenApiVersion = '3.2.0'
    canonicalSourceFiles = $canonicalSourceFiles
    generatedArtifacts = $generatedArtifacts
    pendingProofs = @(
        'SDK package export/import proof',
        'Protocol vector proof'
    )
}

$manifestJson = $manifest | ConvertTo-Json -Depth 10
Write-Utf8NoBomLfFile $manifestPath "$manifestJson`n"

Write-Host "Generated $([IO.Path]::GetRelativePath($repoRoot, $bundlePath))."
Write-Host "Generated $([IO.Path]::GetRelativePath($repoRoot, $projectionPath))."
Write-Host "Generated $([IO.Path]::GetRelativePath($repoRoot, $lossReportPath))."
Write-Host "Updated $([IO.Path]::GetRelativePath($repoRoot, $manifestPath))."
