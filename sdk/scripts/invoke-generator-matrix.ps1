[CmdletBinding()]
param(
    [switch] $SkipGeneration,
    [switch] $SkipProbes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OpenApiGeneratorImage = 'openapitools/openapi-generator-cli:v7.22.0'
$KiotaVersion = '1.32.1'

function Get-RepoRoot {
    $root = git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
        throw 'Unable to resolve repository root with git rev-parse.'
    }

    return $root.Trim()
}

function Get-CommandVersionLine {
    param(
        [string] $Command,
        [string[]] $Arguments
    )

    $resolved = Get-Command $Command -ErrorAction SilentlyContinue
    if ($null -eq $resolved) {
        return 'unavailable'
    }

    $output = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -and $Command -eq 'java') {
        return 'unavailable'
    }

    $line = $output | Select-Object -First 1
    if ($null -eq $line) {
        return 'available'
    }

    return $line.ToString().Trim()
}

function Invoke-CapturedNative {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [string] $WorkingDirectory
    )

    $oldLocation = Get-Location
    Set-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $FilePath @ArgumentList 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Set-Location $oldLocation
    }

    return [pscustomobject]@{
        exitCode = $exitCode
        output = @($output | ForEach-Object { $_.ToString() })
    }
}

function Invoke-CapturedScript {
    param(
        [scriptblock] $Script,
        [string] $WorkingDirectory
    )

    $oldLocation = Get-Location
    Set-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $Script 2>&1
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            $exitCode = 0
        }
    }
    catch {
        $output = @($_.Exception.Message)
        $exitCode = 1
    }
    finally {
        Set-Location $oldLocation
    }

    return [pscustomobject]@{
        exitCode = $exitCode
        output = @($output | ForEach-Object { $_.ToString() })
    }
}

function Select-EvidenceLines {
    param([string[]] $Lines)

    $patterns = @(
        'Errors:',
        'Warnings:',
        'not of type',
        'Cannot populate JSON array',
        'JsonSerializationException',
        'schema must be a map/object',
        'Schema  property .* is null',
        'OpenAPI 3.1 support is still in beta',
        'Finished `dev` profile',
        'found 0 vulnerabilities',
        'ApiClient',
        '^0\.0\.0$',
        '__version__',
        'tsc'
    )

    $evidence = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        foreach ($pattern in $patterns) {
            if ($line -match $pattern) {
                $evidenceLine = $line.Trim()
                if ($evidenceLine -match '^Finished `dev` profile .* target\(s\) in ') {
                    $evidenceLine = 'Finished `dev` profile [unoptimized + debuginfo] target(s)'
                }

                $evidence.Add($evidenceLine)
                break
            }
        }
    }

    return @($evidence | Select-Object -First 40)
}

function Remove-OwnedOutput {
    param(
        [string] $RepoRoot,
        [string] $RelativePath
    )

    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    $resolvedPath = (Resolve-Path -LiteralPath $path).Path
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove outside the repository: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function New-Directory {
    param([string] $Path)

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Write-GeneratedOutputAttributes {
    param([string] $RepoRoot)

    $attributesPath = Join-Path $RepoRoot 'sdk\generated\matrix\openapi-generator\.gitattributes'
    New-Directory (Split-Path -Parent $attributesPath)
    $content = @(
        '# OpenAPI Generator prototype output is committed as raw generator evidence.',
        '# Do not hand-edit generated files to satisfy repository whitespace style.',
        '** -whitespace',
        ''
    ) -join "`n"
    [System.IO.File]::WriteAllText($attributesPath, $content, [System.Text.UTF8Encoding]::new($false))
}

function Get-DirectoryManifestHash {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
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
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($manifestText)
    $sha = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($sha).ToLowerInvariant()
}

function Get-KiotaPath {
    param([string] $RepoRoot)

    $toolPath = Join-Path $env:TEMP 'grace-tools-221\kiota'
    $kiotaPath = Join-Path $toolPath 'kiota.exe'
    if (-not (Test-Path -LiteralPath $kiotaPath -PathType Leaf)) {
        New-Directory $toolPath
        dotnet tool install Microsoft.OpenApi.Kiota --version $KiotaVersion --tool-path $toolPath | Out-Host
    }

    return $kiotaPath
}

function Invoke-OpenApiGenerator {
    param(
        [string] $RepoRoot,
        [string] $GeneratorName,
        [string] $OutputRelativePath,
        [string[]] $AdditionalArguments
    )

    $dockerArgs = @(
        'run',
        '--rm',
        '-v',
        "${RepoRoot}:/local",
        $OpenApiGeneratorImage,
        'generate',
        '-i',
        '/local/src/OpenAPI/Grace.OpenAPI.3.1.2.yaml',
        '-g',
        $GeneratorName,
        '-o',
        "/local/$($OutputRelativePath -replace '\\', '/')"
    ) + $AdditionalArguments

    return Invoke-CapturedNative -FilePath 'docker' -ArgumentList $dockerArgs -WorkingDirectory $RepoRoot
}

function New-MatrixEntry {
    param(
        [string] $Generator,
        [string] $Language,
        [string] $Command,
        [object] $Result,
        [string] $Outcome,
        [string] $Conclusion,
        [string] $TransportPolicy,
        [string] $FacadeFit,
        [string] $OutputPath = ''
    )

    return [ordered]@{
        generator = $Generator
        language = $Language
        command = $Command
        exitCode = $Result.exitCode
        outcome = $Outcome
        conclusion = $Conclusion
        outputPath = $OutputPath
        evidence = @(Select-EvidenceLines $Result.output)
        transportPolicySupport = $TransportPolicy
        facadeFit = $FacadeFit
    }
}

function Set-MatrixEntryRejected {
    param(
        [object] $Entry,
        [string] $Outcome,
        [string] $Conclusion
    )

    $Entry['outcome'] = $Outcome
    $Entry['conclusion'] = $Conclusion
}

$repoRoot = Get-RepoRoot
$projection = Join-Path $repoRoot 'src\OpenAPI\Grace.OpenAPI.3.1.2.yaml'
$matrixRoot = Join-Path $repoRoot 'sdk\generated\matrix'
$entries = New-Object System.Collections.Generic.List[object]
$probeEntries = New-Object System.Collections.Generic.List[object]
$toolVersions = [ordered]@{
    pwsh = Get-CommandVersionLine 'pwsh' @('--version')
    dotnet = Get-CommandVersionLine 'dotnet' @('--version')
    node = Get-CommandVersionLine 'node' @('--version')
    npm = Get-CommandVersionLine 'npm' @('--version')
    python = Get-CommandVersionLine 'python' @('--version')
    cargo = Get-CommandVersionLine 'cargo' @('--version')
    rustc = Get-CommandVersionLine 'rustc' @('--version')
    docker = Get-CommandVersionLine 'docker' @('--version')
    java = Get-CommandVersionLine 'java' @('--version')
    openApiGenerator = $OpenApiGeneratorImage
}

$kiotaPath = Get-KiotaPath $repoRoot
$toolVersions.kiota = (& $kiotaPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
$npxExecutable = if ($IsWindows) { 'npx.cmd' } else { 'npx' }
$npmExecutable = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
$nswagVersionResult = Invoke-CapturedNative $npxExecutable @('--yes', 'nswag', 'version') $repoRoot
$nswagVersionLine = $nswagVersionResult.output | Where-Object { $_ -match '^NSwag version:' } | Select-Object -First 1
$toolVersions.nswag = if ($null -ne $nswagVersionLine) { $nswagVersionLine.ToString().Trim() } else { 'unavailable' }

New-Directory $matrixRoot

$kiotaCSharp = Invoke-CapturedNative $kiotaPath @(
    'generate',
    '-d',
    $projection,
    '-l',
    'CSharp',
    '-o',
    (Join-Path $env:TEMP 'grace-matrix-probe\kiota-csharp'),
    '-c',
    'GraceRawClient',
    '-n',
    'Grace.Generated.Kiota',
    '--clean-output',
    '--exclude-backward-compatible',
    '--additional-data'
) $repoRoot
$entries.Add((New-MatrixEntry 'Kiota' '.NET' 'kiota generate -l CSharp' $kiotaCSharp 'Rejected' `
    'Full projection fails before generation on malformed schema references/null schema properties.' `
    'RequestAdapter pipeline can carry auth, API version, correlation, and client identity once generation succeeds.' `
    'Good facade candidate after contract cleanup; generated client remains hidden behind handwritten facade.'))

$kiotaTypeScript = Invoke-CapturedNative $kiotaPath @(
    'generate',
    '-d',
    $projection,
    '-l',
    'TypeScript',
    '-o',
    (Join-Path $env:TEMP 'grace-matrix-probe\kiota-typescript'),
    '-c',
    'GraceRawClient',
    '-n',
    'Grace.Generated.Kiota',
    '--clean-output',
    '--exclude-backward-compatible',
    '--additional-data',
    '--disable-validation-rules',
    'All'
) $repoRoot
$entries.Add((New-MatrixEntry 'Kiota' 'TypeScript' 'kiota generate -l TypeScript --disable-validation-rules All' `
    $kiotaTypeScript 'Rejected' `
    'Generation still fails on non-object DirectoryVersion collection items after validation rules are disabled.' `
    'RequestAdapter middleware model is promising, but no generated output was available to compile.' `
    'Facade fit cannot be accepted until generation and import compile.'))

$nswagCSharp = Invoke-CapturedNative $npxExecutable @(
    '--yes',
    'nswag',
    'openapi2csclient',
    "/input:$projection",
    "/output:$($env:TEMP)\grace-matrix-probe\nswag-csharp\GraceRawClient.cs",
    '/namespace:Grace.Generated.NSwag',
    '/classname:GraceRawClient'
) $repoRoot
$entries.Add((New-MatrixEntry 'NSwag' '.NET' 'npx --yes nswag openapi2csclient' $nswagCSharp 'Rejected' `
    'NSwag/NJsonSchema cannot parse array-valued schema properties such as FileVersion.RepositoryId.' `
    'HttpClient partial-class customization would support transport policies if generation succeeded.' `
    'Facade fit is acceptable in principle, but parsing fails before a .NET compile probe.'))

if (-not $SkipGeneration) {
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator'
    Write-GeneratedOutputAttributes $repoRoot

    $openApiTypeScriptPath = 'sdk\generated\matrix\openapi-generator\typescript-fetch'
    $openApiTypeScript = Invoke-OpenApiGenerator $repoRoot 'typescript-fetch' $openApiTypeScriptPath @(
        '--skip-validate-spec',
        '--additional-properties=supportsES6=true,npmName=@grace-vcs/generated-openapi-probe,npmVersion=0.0.0-private'
    )
    $entries.Add((New-MatrixEntry 'OpenAPI Generator' 'TypeScript' `
        'docker run openapitools/openapi-generator-cli:v7.22.0 generate -g typescript-fetch --skip-validate-spec' `
        $openApiTypeScript 'Accepted with guardrails' `
        'Generated output is deterministic and compiles, but only when spec validation is skipped.' `
        'Runtime Configuration supports custom fetch, headers, middleware, and basePath injection.' `
        'Usable as a hidden raw client behind a facade; generated API/model names are not public API.' `
        $openApiTypeScriptPath))

    $openApiPythonPath = 'sdk\generated\matrix\openapi-generator\python'
    $openApiPython = Invoke-OpenApiGenerator $repoRoot 'python' $openApiPythonPath @(
        '--skip-validate-spec',
        '--additional-properties=packageName=grace_generated_openapi_probe,projectName=grace-generated-openapi-probe,packageVersion=0.0.0'
    )
    $entries.Add((New-MatrixEntry 'OpenAPI Generator' 'Python' `
        'docker run openapitools/openapi-generator-cli:v7.22.0 generate -g python --skip-validate-spec' `
        $openApiPython 'Accepted with guardrails' `
        'Generated output imports, but only after accepting spec-validation errors as known contract debt.' `
        'Configuration object exposes host, default headers, API key, bearer token, and user agent hooks.' `
        'Can sit behind a facade, but package layout and generated model names must stay internal.' `
        $openApiPythonPath))

    $openApiRustPath = 'sdk\generated\matrix\openapi-generator\rust'
    $openApiRust = Invoke-OpenApiGenerator $repoRoot 'rust' $openApiRustPath @(
        '--skip-validate-spec',
        '--additional-properties=packageName=grace_generated_openapi_probe,packageVersion=0.0.0'
    )
    $entries.Add((New-MatrixEntry 'OpenAPI Generator' 'Rust' `
        'docker run openapitools/openapi-generator-cli:v7.22.0 generate -g rust --skip-validate-spec' `
        $openApiRust 'Accepted with guardrails' `
        'Rust feasibility is positive at cargo-check level, but naming churn and skipped spec validation keep it deferred.' `
        'Generated configuration supports bearer token, API key, user agent, and reqwest client customization.' `
        'Facade required; raw Rust surface is broad and not ready for stable package publication.' `
        $openApiRustPath))
}
else {
    $existingEvidencePath = Join-Path $matrixRoot 'generator-matrix-evidence.json'
    if (-not (Test-Path -LiteralPath $existingEvidencePath -PathType Leaf)) {
        throw "Cannot skip generation because existing matrix evidence is missing: $existingEvidencePath"
    }

    $existingEvidence = Get-Content -LiteralPath $existingEvidencePath -Raw | ConvertFrom-Json
    $existingProjectionHash = [string] $existingEvidence.sourceProjectionSha256
    $currentProjectionHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $projection).Hash.ToLowerInvariant()
    if ($existingProjectionHash.ToLowerInvariant() -ne $currentProjectionHash) {
        throw "Cannot skip generation because existing matrix evidence is stale for $projection. Expected $existingProjectionHash, actual $currentProjectionHash."
    }

    foreach ($entry in @($existingEvidence.matrix | Where-Object { $_.generator -eq 'OpenAPI Generator' })) {
        $entries.Add([ordered]@{
            generator = [string] $entry.generator
            language = [string] $entry.language
            command = [string] $entry.command
            exitCode = [int] $entry.exitCode
            outcome = [string] $entry.outcome
            conclusion = [string] $entry.conclusion
            outputPath = [string] $entry.outputPath
            evidence = @($entry.evidence)
            transportPolicySupport = [string] $entry.transportPolicySupport
            facadeFit = [string] $entry.facadeFit
        })
    }
}

if (-not $SkipProbes) {
    $npmInstall = Invoke-CapturedNative $npmExecutable @('install', '--ignore-scripts') `
        (Join-Path $repoRoot 'sdk\generated\matrix\openapi-generator\typescript-fetch')
    $npmBuild = Invoke-CapturedNative $npmExecutable @('run', 'build') `
        (Join-Path $repoRoot 'sdk\generated\matrix\openapi-generator\typescript-fetch')
    $probeEntries.Add([ordered]@{
        name = 'TypeScript generated client import/build'
        command = 'npm install --ignore-scripts; npm run build'
        exitCode = $npmInstall.exitCode + $npmBuild.exitCode
        evidence = @((Select-EvidenceLines $npmInstall.output) + (Select-EvidenceLines $npmBuild.output))
    })

    $pythonInstall = Invoke-CapturedNative 'python' @('-m', 'pip', 'install', '-e', '.', '--quiet') `
        (Join-Path $repoRoot 'sdk\generated\matrix\openapi-generator\python')
    $pythonImport = Invoke-CapturedNative 'python' @(
        '-c',
        'import grace_generated_openapi_probe; from grace_generated_openapi_probe.api_client import ApiClient; print(grace_generated_openapi_probe.__version__); print(ApiClient.__name__)'
    ) (Join-Path $repoRoot 'sdk\generated\matrix\openapi-generator\python')
    $probeEntries.Add([ordered]@{
        name = 'Python generated client import/build'
        command = 'python -m pip install -e . --quiet; python -c "import grace_generated_openapi_probe ..."'
        exitCode = $pythonInstall.exitCode + $pythonImport.exitCode
        evidence = @((Select-EvidenceLines $pythonInstall.output) + (Select-EvidenceLines $pythonImport.output))
    })

    $cargoCheck = Invoke-CapturedNative 'cargo' @('check') `
        (Join-Path $repoRoot 'sdk\generated\matrix\openapi-generator\rust')
    $probeEntries.Add([ordered]@{
        name = 'Rust generated client cargo check'
        command = 'cargo check'
        exitCode = $cargoCheck.exitCode
        evidence = @(Select-EvidenceLines $cargoCheck.output)
    })

    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\typescript-fetch\node_modules'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\typescript-fetch\dist'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\typescript-fetch\package-lock.json'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\python\grace_generated_openapi_probe.egg-info'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\python\build'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\python\grace_generated_openapi_probe\__pycache__'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\python\grace_generated_openapi_probe\api\__pycache__'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\python\grace_generated_openapi_probe\models\__pycache__'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\rust\Cargo.lock'
    Remove-OwnedOutput $repoRoot 'sdk\generated\matrix\openapi-generator\rust\target'
}

$deterministicEntries = New-Object System.Collections.Generic.List[object]
foreach ($relativePath in @(
    'sdk\generated\matrix\openapi-generator\typescript-fetch',
    'sdk\generated\matrix\openapi-generator\python',
    'sdk\generated\matrix\openapi-generator\rust'
)) {
    $path = Join-Path $repoRoot $relativePath
    $deterministicEntries.Add([ordered]@{
        path = $relativePath.Replace('\', '/')
        manifestSha256 = Get-DirectoryManifestHash $path
    })
}

$matrixEntries = @($entries.ToArray())
$probes = @($probeEntries.ToArray())
$deterministicRegeneration = @($deterministicEntries.ToArray())
$acceptanceFailures = New-Object System.Collections.Generic.List[string]

foreach ($entry in $matrixEntries) {
    if ($entry.generator -eq 'OpenAPI Generator' -and $entry.outcome -eq 'Accepted with guardrails' -and
        $entry.exitCode -ne 0) {
        Set-MatrixEntryRejected $entry 'Rejected after generation failure' `
            'Generation command failed, so this accepted proof point is not satisfied.'
        $acceptanceFailures.Add("$($entry.generator) $($entry.language) generation failed with exit code $($entry.exitCode).")
    }
}

if ($SkipProbes) {
    foreach ($entry in $matrixEntries) {
        if ($entry.generator -eq 'OpenAPI Generator' -and $entry.outcome -eq 'Accepted with guardrails') {
            Set-MatrixEntryRejected $entry 'Not accepted; probes skipped' `
                'Compile/import probes were skipped, so this accepted proof point is not satisfied.'
            $acceptanceFailures.Add("$($entry.generator) $($entry.language) probes were skipped.")
        }
    }
}
else {
    $probeExpectations = @(
        @{ name = 'TypeScript generated client import/build'; language = 'TypeScript' },
        @{ name = 'Python generated client import/build'; language = 'Python' },
        @{ name = 'Rust generated client cargo check'; language = 'Rust' }
    )

    foreach ($expectation in $probeExpectations) {
        $probe = $probes | Where-Object { $_.name -eq $expectation.name } | Select-Object -First 1
        $entry = $matrixEntries |
            Where-Object { $_.generator -eq 'OpenAPI Generator' -and $_.language -eq $expectation.language } |
            Select-Object -First 1

        if ($null -eq $probe) {
            if ($null -ne $entry -and $entry.outcome -eq 'Accepted with guardrails') {
                Set-MatrixEntryRejected $entry 'Not accepted; probe missing' `
                    'Required compile/import probe did not run, so this accepted proof point is not satisfied.'
            }

            $acceptanceFailures.Add("$($expectation.language) accepted proof point is missing its required probe.")
            continue
        }

        if ($probe.exitCode -ne 0) {
            if ($null -ne $entry -and $entry.outcome -eq 'Accepted with guardrails') {
                Set-MatrixEntryRejected $entry 'Rejected after probe failure' `
                    "Required $($expectation.name) probe failed, so this accepted proof point is not satisfied."
            }

            $acceptanceFailures.Add("$($expectation.language) probe failed with exit code $($probe.exitCode).")
        }
    }
}

$overallConclusion = if ($acceptanceFailures.Count -eq 0) {
    'OpenAPI Generator is the only evaluated tool that produced compile/importable TS, Python, and Rust output, and only with --skip-validate-spec. Kiota and NSwag are rejected until OpenAPI schema-shape debt is fixed.'
}
else {
    "Accepted OpenAPI Generator proof points were not satisfied: $($acceptanceFailures -join '; ') Kiota and NSwag remain rejected until OpenAPI schema-shape debt is fixed."
}

$residualRisks = New-Object System.Collections.Generic.List[string]
$residualRisks.Add(
    'OpenAPI validation errors remain in DirectoryVersion, FileVersion, DiffPiece, FileDiff, FileSystemDifference, and GraceResult shapes.'
)

if ($acceptanceFailures.Count -eq 0) {
    $residualRisks.Add(
        'OpenAPI Generator output is accepted only as a raw-client proof point behind facades, not as a stable package surface.'
    )
    $residualRisks.Add('Rust feasibility is proven at cargo-check level, but Rust facade support remains deferred.')
}
else {
    $residualRisks.Add(
        'OpenAPI Generator raw-client proof points are not accepted until generation and all required compile/import probes pass.'
    )
    $residualRisks.Add('Rust feasibility is not proven by this run until the Rust cargo-check probe passes.')
}

$acceptedTier = if ($acceptanceFailures.Count -eq 0) {
    'raw-openapi-generator-behind-facades'
}
else {
    'none'
}

$pendingSdkGradeRationale = if ($acceptanceFailures.Count -eq 0) {
    'OpenAPI Generator TypeScript, Python, and Rust compile/import probes pass only with --skip-validate-spec; Kiota and NSwag remain rejected on schema-shape debt, so no SDK-grade generated-client package is accepted.'
}
else {
    'At least one accepted OpenAPI Generator raw-client proof point failed generation or compile/import probing; no generated-client tier is accepted by this run.'
}

$traceability = [ordered]@{
    'B-043' = 'OpenAPI proof now validates committed generator matrix evidence as the raw generated-client acceptance path.'
    'B-044' = 'Stable SDK package export/import proof remains pending until package metadata, facade exports, import execution, and generator isolation are verified.'
    'drift-002' = 'Canonical source and generator projection SHA-256 values are rechecked before accepting matrix evidence.'
    'drift-006' = 'Kiota and NSwag rejection evidence is retained and prevents any SDK-grade generated-client claim.'
    'drift-008' = 'OpenAPI Generator TypeScript, Python, and Rust compile/import probes define the accepted guardrailed raw-client tier.'
}

$evidence = [ordered]@{
    schemaVersion = 1
    issue = 221
    sourceProjection = 'src/OpenAPI/Grace.OpenAPI.3.1.2.yaml'
    sourceProjectionSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $projection).Hash.ToLowerInvariant()
    generatedAtPolicy = 'not recorded; report is deterministic across machines except tool output directories'
    toolVersions = $toolVersions
    matrix = $matrixEntries
    probes = $probes
    deterministicRegeneration = $deterministicRegeneration
    overallConclusion = $overallConclusion
    acceptedTier = $acceptedTier
    sdkGradeGeneratedClientAccepted = $false
    pendingSdkGradeRationale = $pendingSdkGradeRationale
    traceability = $traceability
    residualRisks = @($residualRisks.ToArray())
}

$evidencePath = Join-Path $matrixRoot 'generator-matrix-evidence.json'
$evidenceJson = $evidence | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($evidencePath, ($evidenceJson -replace "`r`n", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $([IO.Path]::GetRelativePath($repoRoot, $evidencePath))."

if ($acceptanceFailures.Count -gt 0) {
    throw "Generator matrix accepted proof points failed: $($acceptanceFailures -join '; ')"
}
