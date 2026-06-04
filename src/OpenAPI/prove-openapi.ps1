[CmdletBinding()]
param(
    [ValidateSet('All', 'Freshness', 'CanonicalVersion', 'Projections', 'Quality', 'SdkPackage', 'ProtocolVectors')]
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
        [pscustomobject]@{ File = 'Diff.Paths.OpenAPI.yaml'; OperationId = 'GetDiffBySha256Hash'; Tag = 'Diffs' }
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
    }

    foreach ($expected in $expectedOperations) {
        $operation = Get-RequiredOpenApiOperation $Operations $expected.File $expected.OperationId

        Assert-OperationTextMatches `
            $operation `
            "(?s)tags:\s*-\s+$($expected.Tag)\b" `
            "Operation '$($expected.OperationId)' must carry primary SDK tag '$($expected.Tag)'."
    }

    $branchComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Branch.Components.OpenAPI.yaml') -Raw
    foreach ($requiredBranchContract in @(
            'DirectoryVersionId:',
            'BranchReturnValue:',
            'ReferenceReturnValue:',
            'ReferenceListReturnValue:'
        )) {
        Assert-TextContains $branchComponentsText $requiredBranchContract "Branch OpenAPI components are missing '$requiredBranchContract'."
    }

    $diffComponentsText = Get-Content -LiteralPath (Join-Path $OpenApiRoot 'Diff.Components.OpenAPI.yaml') -Raw
    foreach ($requiredDiffContract in @(
            'DirectoryVersionId1:',
            'DirectoryVersionId2:',
            'DiffReturnValue:',
            'DiffPopulateReturnValue:'
        )) {
        Assert-TextContains $diffComponentsText $requiredDiffContract "Diff OpenAPI components are missing '$requiredDiffContract'."
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
        Add-Pending 'No SDK package export/import proof exists; no SDK package or tier is accepted by this slice.'
        return
    }

    Add-Pending "SDK package export/import proof is not accepted yet. A future verifier must validate package metadata, exported facade surface, import execution evidence, and generator isolation before this gate can pass. Placeholder path: $packageProofPath"
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
        Test-SdkPackageProof $repoRoot
        Test-ProtocolVectorProof $repoRoot
    }
    'Freshness' { Test-OpenApiFreshness $repoRoot }
    'CanonicalVersion' { Test-OpenApiCanonicalVersion $repoRoot }
    'Projections' { Test-OpenApiProjections $repoRoot }
    'Quality' { Test-OpenApiQuality $repoRoot }
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
