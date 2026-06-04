[CmdletBinding()]
param(
    [ValidateSet('All', 'Freshness', 'Quality', 'SdkPackage', 'ProtocolVectors')]
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

function Test-OpenApiFreshness {
    param([string] $RepoRoot)

    Write-Host '== OpenAPI freshness =='
    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $manifestPath = Join-Path $openApiRoot 'OpenAPI.ProofManifest.json'

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Failure "Missing OpenAPI proof manifest: $manifestPath"
        return
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        Add-Failure "Unsupported OpenAPI proof manifest schemaVersion '$($manifest.schemaVersion)'."
    }

    $expectedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.canonicalSourceFiles) {
        [void] $expectedFiles.Add([string] $entry.path)
        $sourcePath = Join-Path $openApiRoot ([string] $entry.path)

        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            Add-Failure "Manifest source file is missing: $($entry.path)"
            continue
        }

        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()
        if ($actualHash -ne ([string] $entry.sha256).ToLowerInvariant()) {
            Add-Failure "OpenAPI source hash is stale for $($entry.path). Expected $($entry.sha256), actual $actualHash."
        }
    }

    $actualFiles = Get-ChildItem -LiteralPath $openApiRoot -Filter '*.OpenAPI.yaml' -File |
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
        $declaredArtifacts = ($manifest.generatedArtifacts | ForEach-Object { [string] $_.path }) -join ', '
        Add-Pending "Generated artifact freshness is not accepted yet. Declared artifacts require a future verifier that recomputes source digests, validates generator/tool-version provenance, and rejects hand-edited derived output. Declared: $declaredArtifacts"
    }

    Add-Pass "OpenAPI proof manifest covers $($expectedFiles.Count) canonical source files."
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
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            $pathMatch = [regex]::Match($line, "^\s*'?(?<path>/[^']*)'?:\s*$")
            if ($pathMatch.Success) {
                $currentPath = $pathMatch.Groups['path'].Value
                continue
            }

            $methodMatch = [regex]::Match($line, '^\s{2}(?<method>get|put|post|delete|patch|head|options|trace):\s*$')
            if (-not $methodMatch.Success -or $null -eq $currentPath) {
                continue
            }

            $method = $methodMatch.Groups['method'].Value
            if (-not $methods.Contains($method)) {
                continue
            }

            $operationLines = [System.Collections.Generic.List[string]]::new()
            for ($cursor = $index + 1; $cursor -lt $lines.Count; $cursor++) {
                $nextLine = $lines[$cursor]
                if ([regex]::IsMatch($nextLine, "^\s*'?(?<path>/[^']*)'?:\s*$")) {
                    break
                }

                if ([regex]::IsMatch($nextLine, '^\s{2}(get|put|post|delete|patch|head|options|trace):\s*$')) {
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
                HasTag = $operationText -match "(?m)^\s+tags:\s*$" -and $operationText -match "(?m)^\s+-\s+[A-Za-z][A-Za-z0-9 ._-]*\s*$"
                HasResponses = $responsesMatch.Success
                Has400 = $operationText -match "(?m)^\s+'400':\s*$"
                Has500 = $operationText -match "(?m)^\s+'500':\s*$"
            })
        }
    }

    return $operations
}

function Test-OpenApiQuality {
    param([string] $RepoRoot)

    Write-Host '== OpenAPI quality =='
    $openApiRoot = Get-OpenApiRoot $RepoRoot
    $operations = Get-OpenApiOperations $openApiRoot

    if ($operations.Count -eq 0) {
        Add-Failure 'No OpenAPI operations were found.'
        return
    }

    $missingOperationId = @($operations | Where-Object { [string]::IsNullOrWhiteSpace($_.OperationId) })
    foreach ($operation in $missingOperationId) {
        Add-Failure "Missing operationId: $($operation.File):$($operation.LineNumber) $($operation.Method.ToUpperInvariant()) $($operation.Path)"
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

    $headersFile = Join-Path $openApiRoot 'Transport.Headers.OpenAPI.yaml'
    if (-not (Test-Path -LiteralPath $headersFile -PathType Leaf)) {
        Add-Pending 'Reusable transport header components are not represented yet; no header contract is accepted.'
    }

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
        Test-OpenApiQuality $repoRoot
        Test-SdkPackageProof $repoRoot
        Test-ProtocolVectorProof $repoRoot
    }
    'Freshness' { Test-OpenApiFreshness $repoRoot }
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
