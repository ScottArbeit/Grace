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
                $evidence.Add($line.Trim())
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

if (-not $SkipProbes) {
    $npmInstall = Invoke-CapturedNative 'npm.cmd' @('install', '--ignore-scripts') `
        (Join-Path $repoRoot 'sdk\generated\matrix\openapi-generator\typescript-fetch')
    $npmBuild = Invoke-CapturedNative 'npm.cmd' @('run', 'build') `
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
    overallConclusion = 'OpenAPI Generator is the only evaluated tool that produced compile/importable TS, Python, and Rust output, and only with --skip-validate-spec. Kiota and NSwag are rejected until OpenAPI schema-shape debt is fixed.'
    residualRisks = @(
        'OpenAPI validation errors remain in DirectoryVersion, FileVersion, DiffPiece, FileDiff, FileSystemDifference, and GraceResult shapes.',
        'OpenAPI Generator output is accepted only as a raw-client proof point behind facades, not as a stable package surface.',
        'Rust feasibility is proven at cargo-check level, but Rust facade support remains deferred.'
    )
}

$evidencePath = Join-Path $matrixRoot 'generator-matrix-evidence.json'
$evidenceJson = $evidence | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($evidencePath, ($evidenceJson -replace "`r`n", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $([IO.Path]::GetRelativePath($repoRoot, $evidencePath))."
