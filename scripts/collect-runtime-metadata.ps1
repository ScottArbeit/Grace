[CmdletBinding()]
param(
    [string]$WorkspacePath = (Get-Location).Path,
    [string]$StatusText,
    [string]$StatusFile,
    [ValidateSet("Object", "Json", "Yaml")]
    [string]$OutputFormat = "Json",
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-FieldRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    [ordered]@{
        name = $Name
        value = "unknown"
        source = "unknown"
        evidence = "not found"
        confidence = "low"
    }
}

function Set-FieldValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Field,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Evidence,
        [ValidateSet("low", "medium", "high")]
        [string]$Confidence = "medium",
        [switch]$Force
    )

    if (-not $Force -and $Field.value -ne "unknown" -and -not [string]::IsNullOrWhiteSpace($Field.value)) {
        Write-Verbose ("Skipping update for [{0}] because a value already exists from [{1}]." -f $Field.name, $Field.source)
        return
    }

    $normalizedValue =
        if ([string]::IsNullOrWhiteSpace($Value)) {
            "unknown"
        } else {
            $Value.Trim()
        }

    $Field.value = $normalizedValue
    $Field.source = $Source
    $Field.evidence = $Evidence
    $Field.confidence = $Confidence

    Write-Verbose (
        "Captured field [{0}] = [{1}] from [{2}] (confidence: {3}). Evidence: {4}" -f
        $Field.name,
        $Field.value,
        $Field.source,
        $Field.confidence,
        $Field.evidence
    )
}

function Resolve-TextValue {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string]) {
        $text = $Value.Trim().Trim("'`"")
        if ([string]::IsNullOrWhiteSpace($text)) {
            return $null
        }

        return $text
    }

    return ([string]$Value).Trim()
}

function Get-ProcessAncestors {
    $ancestors = New-Object System.Collections.Generic.List[object]

    try {
        $process = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $PID)
    } catch {
        Write-Verbose ("Unable to inspect process tree: {0}" -f $_.Exception.Message)
        return @()
    }

    $depth = 0
    while ($null -ne $process -and $depth -lt 15) {
        $record = [pscustomobject]@{
            depth = $depth
            name = $process.Name
            pid = $process.ProcessId
            parentPid = $process.ParentProcessId
            commandLine = $process.CommandLine
        }
        $ancestors.Add($record)

        Write-Verbose (
            "Process ancestor depth={0} name={1} pid={2} parentPid={3}" -f
            $record.depth,
            $record.name,
            $record.pid,
            $record.parentPid
        )

        if (-not $process.ParentProcessId) {
            break
        }

        try {
            $process = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $process.ParentProcessId)
        } catch {
            break
        }
        $depth++
    }

    return $ancestors
}

function Get-HarnessFromProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Ancestors
    )

    $patterns = @(
        @{ Regex = "codex"; Harness = "codex"; Confidence = "high" },
        @{ Regex = "claude"; Harness = "claude"; Confidence = "high" },
        @{ Regex = "cursor"; Harness = "cursor"; Confidence = "high" },
        @{ Regex = "copilot"; Harness = "copilot"; Confidence = "high" },
        @{ Regex = "gemini"; Harness = "gemini"; Confidence = "high" }
    )

    foreach ($ancestor in $Ancestors) {
        $name = [string]$ancestor.name
        $cmd = [string]$ancestor.commandLine
        foreach ($pattern in $patterns) {
            if ($name -match $pattern.Regex -or $cmd -match $pattern.Regex) {
                return [ordered]@{
                    harness = $pattern.Harness
                    evidence = ("process tree: {0} (pid {1})" -f $ancestor.name, $ancestor.pid)
                    confidence = $pattern.Confidence
                }
            }
        }
    }

    return $null
}

function Get-HarnessFromEnvironment {
    $envCandidates = @(
        @{ Name = "CODEX_THREAD_ID"; Harness = "codex" },
        @{ Name = "CODEX_MANAGED_BY_NPM"; Harness = "codex" },
        @{ Name = "CLAUDE_CODE"; Harness = "claude" },
        @{ Name = "CURSOR_TRACE_ID"; Harness = "cursor" },
        @{ Name = "GITHUB_COPILOT_TOKEN"; Harness = "copilot" },
        @{ Name = "GEMINI_API_KEY"; Harness = "gemini" }
    )

    foreach ($candidate in $envCandidates) {
        $value = [Environment]::GetEnvironmentVariable($candidate.Name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [ordered]@{
                harness = $candidate.Harness
                evidence = ("environment variable: {0}" -f $candidate.Name)
                confidence = "medium"
            }
        }
    }

    return $null
}

function Get-ProviderFromModel {
    param(
        [string]$Model
    )

    if ([string]::IsNullOrWhiteSpace($Model) -or $Model -eq "unknown") {
        return $null
    }

    $normalized = $Model.ToLowerInvariant()
    $mappings = @(
        @{ Pattern = "^(gpt|o[1-9]|chatgpt|gpt-5|gpt-4|o3|o4|gpt-5\.)"; Provider = "openai" },
        @{ Pattern = "codex"; Provider = "openai" },
        @{ Pattern = "^claude"; Provider = "anthropic" },
        @{ Pattern = "^gemini"; Provider = "google" },
        @{ Pattern = "command-r|cohere"; Provider = "cohere" },
        @{ Pattern = "^mistral"; Provider = "mistral" },
        @{ Pattern = "^deepseek"; Provider = "deepseek" },
        @{ Pattern = "^qwen"; Provider = "alibaba" },
        @{ Pattern = "^grok"; Provider = "xai" },
        @{ Pattern = "llama"; Provider = "meta-or-compatible" },
        @{ Pattern = "^phi"; Provider = "microsoft" }
    )

    foreach ($map in $mappings) {
        if ($normalized -match $map.Pattern) {
            return $map.Provider
        }
    }

    return "unknown"
}

function Get-ProviderFromHarness {
    param(
        [string]$Harness
    )

    switch ($Harness) {
        "codex" { return "openai" }
        "claude" { return "anthropic" }
        "gemini" { return "google" }
        default { return "unknown" }
    }
}

function Convert-ToReasoningEquivalent {
    param(
        [string]$ReasoningLevel
    )

    if ([string]::IsNullOrWhiteSpace($ReasoningLevel) -or $ReasoningLevel -eq "unknown") {
        return "unknown"
    }

    $normalized = $ReasoningLevel.Trim().ToLowerInvariant()

    if ($normalized -in @("low", "medium", "high", "xhigh")) {
        return $normalized
    }

    if ($normalized -in @("false", "off", "disabled", "none")) {
        return "low"
    }

    if ($normalized -in @("true", "on", "enabled")) {
        return "medium"
    }

    if ($normalized -match "\b(minimal|light|fast|quick|low)\b") {
        return "low"
    }

    if ($normalized -match "\b(balanced|standard|normal|default|medium)\b") {
        return "medium"
    }

    if ($normalized -match "\b(deep|intensive|high)\b") {
        return "high"
    }

    if ($normalized -match "\b(max|very-high|ultra|extended|xhigh)\b") {
        return "xhigh"
    }

    if ($normalized -match "^\d+$") {
        $budget = [int]$normalized
        if ($budget -le 2000) {
            return "low"
        }

        if ($budget -le 8000) {
            return "medium"
        }

        if ($budget -le 24000) {
            return "high"
        }

        return "xhigh"
    }

    return "unknown"
}

function Get-CandidateConfigPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkspaceRoot
    )

    $homeDir = [Environment]::GetFolderPath("UserProfile")
    $appData = [Environment]::GetFolderPath("ApplicationData")
    $localAppData = [Environment]::GetFolderPath("LocalApplicationData")

    $candidates = New-Object System.Collections.Generic.List[string]
    $known = @(
        "$homeDir\.codex\config.toml",
        "$homeDir\.claude\settings.json",
        "$homeDir\.claude\config.json",
        "$homeDir\.gemini\settings.json",
        "$homeDir\.gemini\config.json",
        "$homeDir\.copilot\config.json",
        "$homeDir\.config\codex\config.toml",
        "$homeDir\.config\claude\settings.json",
        "$homeDir\.config\gemini\config.json",
        "$homeDir\.config\copilot\config.json",
        "$homeDir\.config\github-copilot\config.json",
        "$homeDir\.config\cursor\settings.json",
        "$appData\Cursor\User\settings.json",
        "$localAppData\Programs\cursor\resources\app\settings.json",
        "$WorkspaceRoot\.codex\config.toml",
        "$WorkspaceRoot\.claude\settings.json",
        "$WorkspaceRoot\.gemini\settings.json",
        "$WorkspaceRoot\.copilot\config.json",
        "$WorkspaceRoot\.cursor\settings.json",
        "$WorkspaceRoot\.vscode\settings.json"
    )

    foreach ($path in $known) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        if ($candidates.Contains($path)) {
            continue
        }

        $candidates.Add($path)
    }

    return $candidates
}

function Get-KeyMatchFromText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string[]]$Keys
    )

    foreach ($key in $Keys) {
        $pattern = "(?im)^\s*[""'']?{0}[""'']?\s*[:=]\s*[""'']?(?<value>[^`r`n#,""']+)" -f [Regex]::Escape($key)
        $match = [Regex]::Match($Text, $pattern)
        if ($match.Success) {
            $value = Resolve-TextValue -Value $match.Groups["value"].Value
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return [ordered]@{
                    key = $key
                    value = $value
                }
            }
        }
    }

    return $null
}

function Get-ConfigExtraction {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $result = [ordered]@{
        path = $Path
        model = $null
        reasoning = $null
        provider = $null
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Verbose ("Config path not found: {0}" -f $Path)
        return $result
    }

    Write-Verbose ("Reading config candidate: {0}" -f $Path)
    $text = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($text)) {
        Write-Verbose ("Config file was empty: {0}" -f $Path)
        return $result
    }

    $modelKeys = @(
        "model",
        "default_model",
        "model_name",
        "engine",
        "deployment",
        "deployment_name",
        "github.copilot.chat.model"
    )
    $reasonKeys = @(
        "model_reasoning_effort",
        "reasoning_level",
        "reasoning_effort",
        "reasoning",
        "thinking",
        "thinking_level",
        "effort"
    )
    $providerKeys = @(
        "provider",
        "model_provider",
        "vendor"
    )

    $modelMatch = Get-KeyMatchFromText -Text $text -Keys $modelKeys
    $reasonMatch = Get-KeyMatchFromText -Text $text -Keys $reasonKeys
    $providerMatch = Get-KeyMatchFromText -Text $text -Keys $providerKeys

    if ($null -ne $modelMatch) {
        $result.model = $modelMatch
        Write-Verbose ("Model match in {0}: {1}={2}" -f $Path, $modelMatch.key, $modelMatch.value)
    }

    if ($null -ne $reasonMatch) {
        $result.reasoning = $reasonMatch
        Write-Verbose ("Reasoning match in {0}: {1}={2}" -f $Path, $reasonMatch.key, $reasonMatch.value)
    }

    if ($null -ne $providerMatch) {
        $result.provider = $providerMatch
        Write-Verbose ("Provider match in {0}: {1}={2}" -f $Path, $providerMatch.key, $providerMatch.value)
    }

    return $result
}

function Parse-StatusMetadata {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    $result = [ordered]@{
        harness = $null
        provider = $null
        model = $null
        reasoning = $null
    }

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $result
    }

    Write-Verbose "Parsing runtime status text."

    $modelLine = [Regex]::Match($Text, "(?im)^\s*(model|active model)\s*:\s*(?<value>[^\r\n]+)")
    if ($modelLine.Success) {
        $raw = $modelLine.Groups["value"].Value.Trim()
        $model = $raw
        $reasoningFromParen = $null

        $paren = [Regex]::Match($raw, "^(?<model>[^(]+)\((?<details>[^)]+)\)")
        if ($paren.Success) {
            $model = $paren.Groups["model"].Value.Trim()
            $details = $paren.Groups["details"].Value
            $reasoningMatch = [Regex]::Match($details, "(?i)reasoning\s+(?<reason>[^,\)]+)")
            if ($reasoningMatch.Success) {
                $reasoningFromParen = $reasoningMatch.Groups["reason"].Value.Trim()
            }
        }

        $result.model = [ordered]@{
            key = "Model"
            value = $model
        }
        if (-not [string]::IsNullOrWhiteSpace($reasoningFromParen)) {
            $result.reasoning = [ordered]@{
                key = "Model(reasoning)"
                value = $reasoningFromParen
            }
        }
    }

    $reasoningLine = [Regex]::Match($Text, "(?im)^\s*(reasoning|effort|thinking)\s*:\s*(?<value>[^\r\n]+)")
    if ($reasoningLine.Success) {
        $result.reasoning = [ordered]@{
            key = $reasoningLine.Groups[1].Value
            value = $reasoningLine.Groups["value"].Value.Trim()
        }
    }

    $harnessLine = [Regex]::Match($Text, "(?im)^\s*>\s*_?\s*(?<value>[^\r\n]+)")
    if ($harnessLine.Success) {
        $header = $harnessLine.Groups["value"].Value.Trim()
        if ($header -match "(?i)\bcodex\b") {
            $result.harness = [ordered]@{
                key = "header"
                value = "codex"
            }
        } elseif ($header -match "(?i)\bclaude\b") {
            $result.harness = [ordered]@{
                key = "header"
                value = "claude"
            }
        } elseif ($header -match "(?i)\bcursor\b") {
            $result.harness = [ordered]@{
                key = "header"
                value = "cursor"
            }
        } elseif ($header -match "(?i)\bcopilot\b") {
            $result.harness = [ordered]@{
                key = "header"
                value = "copilot"
            }
        } elseif ($header -match "(?i)\bgemini\b") {
            $result.harness = [ordered]@{
                key = "header"
                value = "gemini"
            }
        }
    }

    return $result
}

$fields = [ordered]@{
    harness = New-FieldRecord -Name "harness"
    provider = New-FieldRecord -Name "provider"
    model = New-FieldRecord -Name "model"
    reasoning_level = New-FieldRecord -Name "reasoning_level"
}

$checkedSources = New-Object System.Collections.Generic.List[string]

Write-Verbose ("WorkspacePath: {0}" -f $WorkspacePath)
Write-Verbose ("OutputFormat: {0}" -f $OutputFormat)

$runtimeStatus = $null
if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
    if (Test-Path -LiteralPath $StatusFile) {
        Write-Verbose ("Loading status text from file: {0}" -f $StatusFile)
        $runtimeStatus = Get-Content -LiteralPath $StatusFile -Raw
        $checkedSources.Add(("status-file:{0}" -f $StatusFile))
    } else {
        Write-Verbose ("Status file not found: {0}" -f $StatusFile)
    }
}

if ([string]::IsNullOrWhiteSpace($runtimeStatus) -and -not [string]::IsNullOrWhiteSpace($StatusText)) {
    Write-Verbose "Using status text from -StatusText parameter."
    $runtimeStatus = $StatusText
    $checkedSources.Add("status-param")
}

if (-not [string]::IsNullOrWhiteSpace($runtimeStatus)) {
    $status = Parse-StatusMetadata -Text $runtimeStatus
    if ($null -ne $status.harness) {
        Set-FieldValue -Field $fields.harness -Value $status.harness.value -Source "status" -Evidence ("status {0}" -f $status.harness.key) -Confidence high
    }
    if ($null -ne $status.model) {
        Set-FieldValue -Field $fields.model -Value $status.model.value -Source "status" -Evidence ("status {0}" -f $status.model.key) -Confidence high
    }
    if ($null -ne $status.reasoning) {
        Set-FieldValue -Field $fields.reasoning_level -Value $status.reasoning.value -Source "status" -Evidence ("status {0}" -f $status.reasoning.key) -Confidence high
    }
}

$ancestors = Get-ProcessAncestors
$checkedSources.Add("process-tree")

$harnessFromProcess = Get-HarnessFromProcessTree -Ancestors $ancestors
if ($null -ne $harnessFromProcess) {
    Set-FieldValue -Field $fields.harness -Value $harnessFromProcess.harness -Source "runtime-process" -Evidence $harnessFromProcess.evidence -Confidence $harnessFromProcess.confidence
}

$harnessFromEnv = Get-HarnessFromEnvironment
$checkedSources.Add("environment")
if ($null -ne $harnessFromEnv) {
    Set-FieldValue -Field $fields.harness -Value $harnessFromEnv.harness -Source "environment" -Evidence $harnessFromEnv.evidence -Confidence $harnessFromEnv.confidence
}

$candidatePaths = Get-CandidateConfigPaths -WorkspaceRoot $WorkspacePath
$checkedSources.Add("config-candidates")
Write-Verbose ("Evaluating {0} candidate config path(s)." -f $candidatePaths.Count)

foreach ($path in $candidatePaths) {
    $extract = Get-ConfigExtraction -Path $path
    if ($null -ne $extract.model) {
        Set-FieldValue -Field $fields.model -Value $extract.model.value -Source "config-file" -Evidence ("{0}:{1}" -f $extract.path, $extract.model.key) -Confidence high
    }

    if ($null -ne $extract.reasoning) {
        Set-FieldValue -Field $fields.reasoning_level -Value $extract.reasoning.value -Source "config-file" -Evidence ("{0}:{1}" -f $extract.path, $extract.reasoning.key) -Confidence high
    }

    if ($null -ne $extract.provider) {
        Set-FieldValue -Field $fields.provider -Value $extract.provider.value -Source "config-file" -Evidence ("{0}:{1}" -f $extract.path, $extract.provider.key) -Confidence high
    }
}

$modelEnvKeys = @("MODEL", "LLM_MODEL", "AI_MODEL", "OPENAI_MODEL", "ANTHROPIC_MODEL", "GEMINI_MODEL", "GOOGLE_MODEL")
foreach ($envKey in $modelEnvKeys) {
    $envValue = [Environment]::GetEnvironmentVariable($envKey)
    if (-not [string]::IsNullOrWhiteSpace($envValue)) {
        Set-FieldValue -Field $fields.model -Value $envValue -Source "environment" -Evidence ("env:{0}" -f $envKey) -Confidence medium
        break
    }
}

$reasonEnvKeys = @("REASONING_LEVEL", "REASONING_EFFORT", "OPENAI_REASONING_EFFORT", "THINKING_LEVEL", "THINKING")
foreach ($envKey in $reasonEnvKeys) {
    $envValue = [Environment]::GetEnvironmentVariable($envKey)
    if (-not [string]::IsNullOrWhiteSpace($envValue)) {
        Set-FieldValue -Field $fields.reasoning_level -Value $envValue -Source "environment" -Evidence ("env:{0}" -f $envKey) -Confidence medium
        break
    }
}

if ($fields.provider.value -eq "unknown") {
    $providerFromModel = Get-ProviderFromModel -Model $fields.model.value
    if ($providerFromModel -ne "unknown") {
        Set-FieldValue -Field $fields.provider -Value $providerFromModel -Source "inference-model" -Evidence ("model pattern match: {0}" -f $fields.model.value) -Confidence medium
    }
}

if ($fields.provider.value -eq "unknown") {
    $providerFromHarness = Get-ProviderFromHarness -Harness $fields.harness.value
    if ($providerFromHarness -ne "unknown") {
        Set-FieldValue -Field $fields.provider -Value $providerFromHarness -Source "inference-harness" -Evidence ("harness mapping: {0}" -f $fields.harness.value) -Confidence low
    }
}

$reasoningEquivalent = Convert-ToReasoningEquivalent -ReasoningLevel $fields.reasoning_level.value
$highReasoning = $reasoningEquivalent -in @("high", "xhigh")

$sources = @(
    @($fields.harness.source, $fields.provider.source, $fields.model.source, $fields.reasoning_level.source) |
        Where-Object { $_ -ne "unknown" } |
        Select-Object -Unique
)

$metadataSource =
    if ($sources.Count -eq 0) {
        "unknown"
    } elseif ($sources.Count -eq 1) {
        $sources[0]
    } else {
        "mixed"
    }

$result = [ordered]@{
    harness = $fields.harness.value
    provider = $fields.provider.value
    model = $fields.model.value
    reasoning_level = $fields.reasoning_level.value
    reasoning_level_equivalent = $reasoningEquivalent
    high_reasoning_asserted = [bool]$highReasoning
    latest_model_asserted = $false
    metadata_source = $metadataSource
    metadata_evidence = [ordered]@{
        harness = $fields.harness.evidence
        provider = $fields.provider.evidence
        model = $fields.model.evidence
        reasoning_level = $fields.reasoning_level.evidence
    }
    confidence = [ordered]@{
        harness = $fields.harness.confidence
        provider = $fields.provider.confidence
        model = $fields.model.confidence
        reasoning_level = $fields.reasoning_level.confidence
    }
    checked_sources = @($checkedSources | Select-Object -Unique)
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
}

$output = $null
switch ($OutputFormat) {
    "Object" {
        $output = [pscustomobject]$result
    }
    "Json" {
        $output = $result | ConvertTo-Json -Depth 6
    }
    "Yaml" {
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add(("harness: {0}" -f $result.harness))
        $lines.Add(("provider: {0}" -f $result.provider))
        $lines.Add(("model: {0}" -f $result.model))
        $lines.Add(("reasoning_level: {0}" -f $result.reasoning_level))
        $lines.Add(("reasoning_level_equivalent: {0}" -f $result.reasoning_level_equivalent))
        $lines.Add(("high_reasoning_asserted: {0}" -f $result.high_reasoning_asserted.ToString().ToLowerInvariant()))
        $lines.Add(("latest_model_asserted: {0}" -f $result.latest_model_asserted.ToString().ToLowerInvariant()))
        $lines.Add(("metadata_source: {0}" -f $result.metadata_source))
        $lines.Add("metadata_evidence:")
        $lines.Add(("  harness: {0}" -f $result.metadata_evidence.harness))
        $lines.Add(("  provider: {0}" -f $result.metadata_evidence.provider))
        $lines.Add(("  model: {0}" -f $result.metadata_evidence.model))
        $lines.Add(("  reasoning_level: {0}" -f $result.metadata_evidence.reasoning_level))
        $lines.Add("confidence:")
        $lines.Add(("  harness: {0}" -f $result.confidence.harness))
        $lines.Add(("  provider: {0}" -f $result.confidence.provider))
        $lines.Add(("  model: {0}" -f $result.confidence.model))
        $lines.Add(("  reasoning_level: {0}" -f $result.confidence.reasoning_level))
        $lines.Add("checked_sources:")
        foreach ($source in $result.checked_sources) {
            $lines.Add(("  - {0}" -f $source))
        }
        $lines.Add(("generated_at_utc: {0}" -f $result.generated_at_utc))
        $output = $lines -join [Environment]::NewLine
    }
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputDirectory = Split-Path -Path $OutputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    $serializedOutput =
        if ($OutputFormat -eq "Object") {
            $result | ConvertTo-Json -Depth 6
        } else {
            [string]$output
        }

    Set-Content -LiteralPath $OutputPath -Value $serializedOutput -Encoding utf8NoBOM
    Write-Verbose ("Wrote runtime metadata output to {0}" -f $OutputPath)
}

$output
