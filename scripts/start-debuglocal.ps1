#!/usr/bin/env pwsh
<#
  Canonical local onboarding script for Grace.

  It starts Aspire with the DebugLocal launch profile, waits for Grace.Server
  readiness, optionally probes auth readiness, bootstraps a PAT with TestAuth,
  and prints copy-paste environment commands for PowerShell and bash/zsh shells.

  Run from repo root:
    pwsh ./scripts/start-debuglocal.ps1
#>

[CmdletBinding()]
param(
  [string] $LaunchProfile = "DebugLocal",
  [string] $ProjectPath = "src/Grace.Aspire.AppHost/Grace.Aspire.AppHost.csproj",
  [string] $LaunchSettingsPath = "src/Grace.Aspire.AppHost/Properties/launchSettings.json",
  [string] $GraceServerUri = "http://localhost:5000",
  [string] $BootstrapUserId = "",
  [string] $BootstrapUserIdFallback = "test-admin",
  [string] $TokenName = "local-dev",
  [int]    $TokenDays = 30,
  [int]    $StartupTimeoutSeconds = 240,
  [int]    $TokenBootstrapMaxAttempts = 4,
  [int]    $TokenBootstrapInitialBackoffSeconds = 1,
  [int]    $CleanupWaitSeconds = 5,
  [switch] $NoBuild,
  [switch] $NoTokenBootstrap,
  [switch] $SkipAuthProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:StepCounter = 0
$script:LastFailureInfo = $null

function Write-Step([string] $Message, [string] $Color = "Cyan") {
  $script:StepCounter++
  $timestamp = Get-Date -Format "HH:mm:ss"
  Write-Host "[$timestamp] Step $($script:StepCounter): $Message" -ForegroundColor $Color
}

function Write-Detail([string] $Message, [string] $Color = "DarkGray") {
  Write-Host "  - $Message" -ForegroundColor $Color
}

function Get-FirstUserId([string] $Users) {
  if ([string]::IsNullOrWhiteSpace($Users)) {
    return $null
  }

  $parts = $Users.Split(";", [System.StringSplitOptions]::RemoveEmptyEntries)
  foreach ($part in $parts) {
    $candidate = $part.Trim()
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
      return $candidate
    }
  }

  return $null
}

function Merge-UserIds([string[]] $Values) {
  $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
  $result = [System.Collections.Generic.List[string]]::new()

  foreach ($value in $Values) {
    if ([string]::IsNullOrWhiteSpace($value)) {
      continue
    }

    $parts = $value.Split(";", [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($part in $parts) {
      $candidate = $part.Trim()
      if ([string]::IsNullOrWhiteSpace($candidate)) {
        continue
      }

      if ($seen.Add($candidate)) {
        $result.Add($candidate)
      }
    }
  }

  return [string]::Join(";", $result)
}

function Get-BootstrapUsersFromLaunchProfile([string] $LaunchSettingsFile, [string] $ProfileName) {
  if ([string]::IsNullOrWhiteSpace($LaunchSettingsFile) -or -not (Test-Path $LaunchSettingsFile)) {
    return $null
  }

  try {
    $json = Get-Content -Raw -Path $LaunchSettingsFile | ConvertFrom-Json -Depth 20
    if ($null -eq $json -or $null -eq $json.profiles) {
      return $null
    }

    $profile = $json.profiles.$ProfileName
    if ($null -eq $profile -or $null -eq $profile.environmentVariables) {
      return $null
    }

    $value = [string] $profile.environmentVariables.grace__authz__bootstrap__system_admin_users
    if ([string]::IsNullOrWhiteSpace($value)) {
      return $null
    }

    return $value.Trim()
  } catch {
    Write-Detail "Could not parse launch settings at '$LaunchSettingsFile': $($_.Exception.Message)" "Yellow"
    return $null
  }
}

function Test-GraceHealth([string] $HealthUrl) {
  try {
    $response = Invoke-WebRequest -Uri $HealthUrl -Method GET -TimeoutSec 2
    return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300)
  } catch {
    return $false
  }
}

function Wait-ForGraceServer(
  [string] $HealthUrl,
  [int] $TimeoutSeconds,
  [int] $DelayMilliseconds,
  [System.Diagnostics.Process] $AppHostProcess
) {
  $safeDelayMilliseconds = [Math]::Max($DelayMilliseconds, 250)
  $maxAttempts = [Math]::Max([int][Math]::Ceiling(($TimeoutSeconds * 1000.0) / $safeDelayMilliseconds), 1)

  for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    if (Test-GraceHealth -HealthUrl $HealthUrl) {
      return $true
    }

    if ($null -ne $AppHostProcess -and $AppHostProcess.HasExited) {
      return $false
    }

    if ($attempt -eq 1 -or ($attempt % 10) -eq 0) {
      Write-Detail "Waiting for Grace.Server ($attempt/$maxAttempts): $HealthUrl"
    }

    Start-Sleep -Milliseconds $safeDelayMilliseconds
  }

  return $false
}

function Resolve-PathFromRepoRoot([string] $RepoRoot, [string] $PathValue) {
  if ([System.IO.Path]::IsPathRooted($PathValue)) {
    return (Resolve-Path $PathValue).Path
  }

  return (Resolve-Path (Join-Path $RepoRoot $PathValue)).Path
}

function New-FailureInfo(
  [string] $Stage,
  [string] $Classification,
  [Nullable[int]] $StatusCode,
  [bool] $Retryable,
  [string] $Message,
  [string] $Hint
) {
  return [pscustomobject]@{
    Stage          = $Stage
    Classification = $Classification
    StatusCode     = $StatusCode
    Retryable      = $Retryable
    Message        = $Message
    Hint           = $Hint
  }
}

function Set-LastFailureInfo([object] $FailureInfo) {
  if ($null -ne $FailureInfo) {
    $script:LastFailureInfo = $FailureInfo
  }
}

function Get-HttpStatusCode([System.Management.Automation.ErrorRecord] $ErrorRecord) {
  if ($null -eq $ErrorRecord -or $null -eq $ErrorRecord.Exception) {
    return $null
  }

  $response = $null
  if ($ErrorRecord.Exception.PSObject.Properties.Match("Response").Count -gt 0) {
    $response = $ErrorRecord.Exception.Response
  }

  if ($null -eq $response) {
    return $null
  }

  if ($response.PSObject.Properties.Match("StatusCode").Count -eq 0) {
    return $null
  }

  try {
    return [int]$response.StatusCode
  } catch {
    return $null
  }
}

function Get-WebFailureInfo(
  [System.Management.Automation.ErrorRecord] $ErrorRecord,
  [string] $Stage,
  [string] $GraceServerUri,
  [string] $BootstrapUserId
) {
  $message = "Unknown error."
  if ($null -ne $ErrorRecord -and $null -ne $ErrorRecord.Exception -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.Exception.Message)) {
    $message = $ErrorRecord.Exception.Message.Trim()
  }

  $statusCode = Get-HttpStatusCode -ErrorRecord $ErrorRecord
  $classification = "unknown"
  $retryable = $false
  $hint = "Check AppHost logs and runtime metadata snapshot for details."

  if ($null -ne $statusCode) {
    if ($statusCode -in @(401, 403)) {
      $classification = "authorization"
      $retryable = $false
      $hint =
        "Ensure '$BootstrapUserId' is in grace__authz__bootstrap__system_admin_users, and verify GRACE_TESTING=1 for local TestAuth."
    } elseif ($statusCode -in @(400, 404)) {
      $classification = "configuration"
      $retryable = $false
      $hint =
        "Verify DebugLocal configuration is active and auth endpoints are available at $GraceServerUri."
    } elseif ($statusCode -in @(408, 425, 429) -or $statusCode -ge 500) {
      $classification = "transient"
      $retryable = $true
      $hint =
        "The server may still be converging. The script will retry bounded transient failures."
    }
  } else {
    if ($message -match "(?i)timed out|timeout|refused|actively refused|connection reset|remote name could not be resolved|no such host|503") {
      $classification = "transient"
      $retryable = $true
      $hint =
        "The server appears unreachable or still starting. Verify startup logs and wait for health readiness."
    } elseif ($message -match "(?i)certificate|ssl|tls") {
      $classification = "configuration"
      $retryable = $false
      $hint =
        "TLS configuration failed for local endpoint. Verify URI and local certificate setup."
    }
  }

  if ($Stage -eq "auth probe" -and $classification -ne "transient") {
    $hint = "$hint You can bypass this preflight with -SkipAuthProbe if intentionally testing without auth probe."
  }

  return New-FailureInfo `
    -Stage $Stage `
    -Classification $classification `
    -StatusCode $statusCode `
    -Retryable $retryable `
    -Message $message `
    -Hint $hint
}

function Get-RetryBackoffSeconds([int] $InitialBackoffSeconds, [int] $AttemptNumber) {
  $safeInitialBackoff = [Math]::Max($InitialBackoffSeconds, 1)
  $power = [Math]::Max($AttemptNumber - 1, 0)
  $delay = [Math]::Min($safeInitialBackoff * [Math]::Pow(2, $power), 20)
  return [int][Math]::Max([Math]::Round($delay), 1)
}

function Invoke-AuthProbe(
  [string] $GraceServerUri,
  [string] $BootstrapUserId
) {
  Write-Step "Running auth readiness probe." "Green"

  $oidcConfigUri = "$GraceServerUri/auth/oidc/config"
  $authMeUri = "$GraceServerUri/auth/me"
  $headers = @{ "x-grace-user-id" = $BootstrapUserId }

  Write-Detail "Probe 1/2: GET $oidcConfigUri"
  try {
    $null = Invoke-WebRequest -Method Get -Uri $oidcConfigUri -TimeoutSec 10
  } catch {
    $failure = Get-WebFailureInfo -ErrorRecord $_ -Stage "auth probe" -GraceServerUri $GraceServerUri -BootstrapUserId $BootstrapUserId
    Set-LastFailureInfo -FailureInfo $failure
    $statusSegment = if ($null -eq $failure.StatusCode) { "no-http-status" } else { [string]$failure.StatusCode }
    throw "Auth probe failed at /auth/oidc/config (classification=$($failure.Classification), status=$statusSegment). $($failure.Hint)"
  }

  Write-Detail "Probe 2/2: GET $authMeUri (x-grace-user-id=$BootstrapUserId)"
  try {
    $null = Invoke-WebRequest -Method Get -Uri $authMeUri -Headers $headers -TimeoutSec 10
  } catch {
    $failure = Get-WebFailureInfo -ErrorRecord $_ -Stage "auth probe" -GraceServerUri $GraceServerUri -BootstrapUserId $BootstrapUserId
    Set-LastFailureInfo -FailureInfo $failure
    $statusSegment = if ($null -eq $failure.StatusCode) { "no-http-status" } else { [string]$failure.StatusCode }
    throw "Auth probe failed at /auth/me (classification=$($failure.Classification), status=$statusSegment). $($failure.Hint)"
  }

  Write-Detail "Auth readiness probe passed." "Green"
}

function Invoke-TokenBootstrapWithRetry(
  [string] $GraceServerUri,
  [string] $BootstrapUserId,
  [string] $RequestBodyJson,
  [int] $MaxAttempts,
  [int] $InitialBackoffSeconds
) {
  $tokenUri = "$GraceServerUri/auth/token/create"
  $headers = @{ "x-grace-user-id" = $BootstrapUserId }
  $safeMaxAttempts = [Math]::Max($MaxAttempts, 1)
  $safeInitialBackoff = [Math]::Max($InitialBackoffSeconds, 1)
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

  for ($attempt = 1; $attempt -le $safeMaxAttempts; $attempt++) {
    Write-Detail "Token bootstrap attempt $attempt/$safeMaxAttempts -> POST $tokenUri"

    try {
      $response = Invoke-RestMethod `
        -Method Post `
        -Uri $tokenUri `
        -Headers $headers `
        -ContentType "application/json" `
        -Body $RequestBodyJson `
        -TimeoutSec 20

      $stopwatch.Stop()
      return [pscustomobject]@{
        Response       = $response
        Attempts       = $attempt
        ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
      }
    } catch {
      $failure = Get-WebFailureInfo -ErrorRecord $_ -Stage "token bootstrap" -GraceServerUri $GraceServerUri -BootstrapUserId $BootstrapUserId
      Set-LastFailureInfo -FailureInfo $failure

      $statusSegment = if ($null -eq $failure.StatusCode) { "no-http-status" } else { [string]$failure.StatusCode }
      Write-Detail "Attempt $attempt failed (classification=$($failure.Classification), status=$statusSegment)." "Yellow"

      $isLastAttempt = $attempt -ge $safeMaxAttempts
      if (-not $failure.Retryable -or $isLastAttempt) {
        $stopwatch.Stop()
        $retrySegment = if ($failure.Retryable) { "retryable" } else { "non-retryable" }
        throw (
          "Token bootstrap failed after $attempt attempt(s) in $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 2))s " +
          "(classification=$($failure.Classification), status=$statusSegment, $retrySegment). $($failure.Hint)"
        )
      }

      $delaySeconds = Get-RetryBackoffSeconds -InitialBackoffSeconds $safeInitialBackoff -AttemptNumber $attempt
      Write-Detail "Transient failure detected. Retrying in $delaySeconds second(s)." "Yellow"
      Start-Sleep -Seconds $delaySeconds
    }
  }

  throw "Token bootstrap exhausted unexpectedly without a terminal response."
}

function Get-DebugLocalDotnetProcesses([string] $ProjectPath, [string] $LaunchProfile) {
  $matches = [System.Collections.Generic.List[object]]::new()

  $normalizedProjectPath = $ProjectPath.Replace("/", "\").ToLowerInvariant()
  $normalizedLaunchProfile = $LaunchProfile.ToLowerInvariant()

  $candidates = @()
  try {
    $candidates = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='dotnet'" -ErrorAction Stop
  } catch {
    Write-Detail "Unable to inspect existing dotnet processes: $($_.Exception.Message)" "Yellow"
    return @()
  }

  foreach ($candidate in $candidates) {
    $commandLine = [string]$candidate.CommandLine
    if ([string]::IsNullOrWhiteSpace($commandLine)) {
      continue
    }

    $normalizedCommandLine = $commandLine.Replace("/", "\").ToLowerInvariant()
    if (-not $normalizedCommandLine.Contains($normalizedProjectPath)) {
      continue
    }

    if (-not $normalizedCommandLine.Contains("--launch-profile")) {
      continue
    }

    if (-not $normalizedCommandLine.Contains($normalizedLaunchProfile)) {
      continue
    }

    $matches.Add([pscustomobject]@{
      ProcessId   = [int]$candidate.ProcessId
      Name        = [string]$candidate.Name
      CommandLine = $commandLine
    })
  }

  return @($matches)
}

function Stop-ProcessWithDiagnostics(
  [int] $ProcessId,
  [int] $WaitSeconds,
  [string] $Context
) {
  $result = [ordered]@{
    Context   = $Context
    ProcessId = $ProcessId
    Success   = $false
    Message   = ""
  }

  $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if ($null -eq $process) {
    $result.Success = $true
    $result.Message = "Process $ProcessId is not running."
    return [pscustomobject]$result
  }

  try {
    Stop-Process -Id $ProcessId -ErrorAction Stop
    Wait-Process -Id $ProcessId -Timeout ([Math]::Max($WaitSeconds, 1)) -ErrorAction SilentlyContinue
  } catch {
    Write-Detail "Graceful stop attempt failed for PID ${ProcessId}: $($_.Exception.Message)" "Yellow"
  }

  $stillRunning = $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
  if ($stillRunning) {
    try {
      Stop-Process -Id $ProcessId -Force -ErrorAction Stop
      Wait-Process -Id $ProcessId -Timeout ([Math]::Max($WaitSeconds, 1)) -ErrorAction SilentlyContinue
    } catch {
      $result.Success = $false
      $result.Message = "Failed to stop process ${ProcessId}: $($_.Exception.Message)"
      return [pscustomobject]$result
    }
  }

  $stillRunning = $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
  if ($stillRunning) {
    $result.Success = $false
    $result.Message = "Process $ProcessId is still running after force-stop attempt."
  } else {
    $result.Success = $true
    $result.Message = "Process $ProcessId stopped successfully."
  }

  return [pscustomobject]$result
}

function Write-LogTail([string] $LogPath, [int] $TailLines = 20) {
  if ([string]::IsNullOrWhiteSpace($LogPath) -or -not (Test-Path $LogPath)) {
    return
  }

  Write-Detail "Recent log tail ($TailLines lines): $LogPath" "Yellow"
  Get-Content -Path $LogPath -Tail $TailLines | ForEach-Object {
    Write-Host "    $_" -ForegroundColor DarkYellow
  }
}

function Save-RuntimeMetadataSnapshot(
  [string] $RepoRoot,
  [string] $ScriptDirectory,
  [string] $LogRoot,
  [string] $RunId,
  [System.Collections.Generic.List[string]] $DiagnosticsNotes
) {
  $metadataScriptPath = Join-Path $ScriptDirectory "collect-runtime-metadata.ps1"
  if (-not (Test-Path $metadataScriptPath)) {
    $DiagnosticsNotes.Add("Runtime metadata script was not found at '$metadataScriptPath'.")
    return $null
  }

  $metadataPath = Join-Path $LogRoot "start-debuglocal-$RunId.runtime-metadata.json"
  try {
    & $metadataScriptPath -WorkspacePath $RepoRoot -OutputFormat Json -OutputPath $metadataPath | Out-Null
    $DiagnosticsNotes.Add("Runtime metadata snapshot written to '$metadataPath'.")
    return $metadataPath
  } catch {
    $DiagnosticsNotes.Add("Failed to write runtime metadata snapshot: $($_.Exception.Message)")
    return $null
  }
}

function Write-FailureDiagnostics(
  [string] $RepoRoot,
  [string] $ScriptDirectory,
  [string] $LogRoot,
  [string] $RunId,
  [string] $GraceServerUri,
  [string] $HealthUrl,
  [string] $BootstrapUserId,
  [string] $StdoutLog,
  [string] $StderrLog,
  [System.Collections.Generic.List[string]] $CleanupNotes,
  [System.Management.Automation.ErrorRecord] $ErrorRecord
) {
  Write-Step "Collecting failure diagnostics." "Red"

  if ($null -eq $script:LastFailureInfo) {
    $fallbackMessage =
      if ($null -ne $ErrorRecord -and $null -ne $ErrorRecord.Exception) {
        $ErrorRecord.Exception.Message
      } else {
        "Unknown failure"
      }

    $script:LastFailureInfo = New-FailureInfo `
      -Stage "startup workflow" `
      -Classification "unknown" `
      -StatusCode $null `
      -Retryable $false `
      -Message $fallbackMessage `
      -Hint "Inspect logs and runtime metadata for root cause."
  }

  Write-Detail "Failure stage: $($script:LastFailureInfo.Stage)" "Red"
  Write-Detail "Failure classification: $($script:LastFailureInfo.Classification)" "Red"
  Write-Detail "Failure message: $($script:LastFailureInfo.Message)" "Red"
  if ($null -ne $script:LastFailureInfo.StatusCode) {
    Write-Detail "HTTP status: $($script:LastFailureInfo.StatusCode)" "Red"
  }
  Write-Detail "Suggested next step: $($script:LastFailureInfo.Hint)" "Yellow"

  $runtimeMetadataPath = Save-RuntimeMetadataSnapshot `
    -RepoRoot $RepoRoot `
    -ScriptDirectory $ScriptDirectory `
    -LogRoot $LogRoot `
    -RunId $RunId `
    -DiagnosticsNotes $CleanupNotes

  if (-not [string]::IsNullOrWhiteSpace($runtimeMetadataPath)) {
    Write-Detail "Runtime metadata: $runtimeMetadataPath" "Yellow"
  }

  if (-not [string]::IsNullOrWhiteSpace($StdoutLog)) {
    Write-Detail "AppHost stdout log: $StdoutLog" "Yellow"
    Write-LogTail -LogPath $StdoutLog -TailLines 20
  }

  if (-not [string]::IsNullOrWhiteSpace($StderrLog)) {
    Write-Detail "AppHost stderr log: $StderrLog" "Yellow"
    Write-LogTail -LogPath $StderrLog -TailLines 20
  }

  $failureSummary = [ordered]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    grace_server_uri = $GraceServerUri
    health_url = $HealthUrl
    bootstrap_user_id = $BootstrapUserId
    stage = $script:LastFailureInfo.Stage
    classification = $script:LastFailureInfo.Classification
    status_code = $script:LastFailureInfo.StatusCode
    message = $script:LastFailureInfo.Message
    hint = $script:LastFailureInfo.Hint
    stdout_log = $StdoutLog
    stderr_log = $StderrLog
    runtime_metadata = $runtimeMetadataPath
    cleanup_notes = @($CleanupNotes)
  }

  $summaryPath = Join-Path $LogRoot "start-debuglocal-$RunId.failure.json"
  try {
    $failureSummary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding utf8NoBOM
    Write-Detail "Failure summary JSON: $summaryPath" "Yellow"
  } catch {
    Write-Detail "Failed to write failure summary JSON: $($_.Exception.Message)" "Yellow"
  }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
Set-Location $repoRoot

$logRoot = Join-Path $repoRoot ".grace/logs"
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$resolvedProjectPath = ""
$resolvedLaunchSettingsPath = ""
$healthUrl = "$GraceServerUri/healthz"
$appHostProcess = $null
$stdoutLog = ""
$stderrLog = ""
$resolvedBootstrapUserId = ""
$resolvedBootstrapSource = ""
$cleanupNotes = [System.Collections.Generic.List[string]]::new()
$startedAppHostThisRun = $false
$capturedError = $null
$failed = $false

try {
  $resolvedProjectPath = Resolve-PathFromRepoRoot -RepoRoot $repoRoot -PathValue $ProjectPath
  $resolvedLaunchSettingsPath = Resolve-PathFromRepoRoot -RepoRoot $repoRoot -PathValue $LaunchSettingsPath
  $healthUrl = "$GraceServerUri/healthz"

  Write-Step "Preparing DebugLocal startup context."
  Write-Detail "Canonical script: pwsh ./scripts/start-debuglocal.ps1"
  Write-Detail "Compatibility alias: pwsh ./scripts/dev-local.ps1"
  Write-Detail "Repo root: $repoRoot"
  Write-Detail "Project path: $resolvedProjectPath"
  Write-Detail "Launch settings path: $resolvedLaunchSettingsPath"
  Write-Detail "Launch profile: $LaunchProfile"
  Write-Detail "Grace server URI: $GraceServerUri"
  Write-Detail "Startup timeout (seconds): $StartupTimeoutSeconds"
  Write-Detail "Token max attempts: $TokenBootstrapMaxAttempts"
  Write-Detail "Token initial backoff (seconds): $TokenBootstrapInitialBackoffSeconds"
  Write-Detail "NoBuild: $NoBuild"
  Write-Detail "NoTokenBootstrap: $NoTokenBootstrap"
  Write-Detail "SkipAuthProbe: $SkipAuthProbe"

  Write-Step "Resolving bootstrap user identity with deterministic precedence."

  $trimmedParameterBootstrapUserId =
    if ([string]::IsNullOrWhiteSpace($BootstrapUserId)) { "" } else { $BootstrapUserId.Trim() }

  if (-not [string]::IsNullOrWhiteSpace($trimmedParameterBootstrapUserId)) {
    $resolvedBootstrapUserId = $trimmedParameterBootstrapUserId
    $resolvedBootstrapSource = "script parameter (-BootstrapUserId)"
  }

  $existingBootstrapUsers = $env:grace__authz__bootstrap__system_admin_users
  $firstEnvBootstrapUser = Get-FirstUserId $existingBootstrapUsers
  if ([string]::IsNullOrWhiteSpace($resolvedBootstrapUserId) -and -not [string]::IsNullOrWhiteSpace($firstEnvBootstrapUser)) {
    $resolvedBootstrapUserId = $firstEnvBootstrapUser
    $resolvedBootstrapSource = "environment variable (grace__authz__bootstrap__system_admin_users)"
  }

  $launchProfileBootstrapUsers = Get-BootstrapUsersFromLaunchProfile -LaunchSettingsFile $resolvedLaunchSettingsPath -ProfileName $LaunchProfile
  $firstLaunchProfileBootstrapUser = Get-FirstUserId $launchProfileBootstrapUsers
  if ([string]::IsNullOrWhiteSpace($resolvedBootstrapUserId) -and -not [string]::IsNullOrWhiteSpace($firstLaunchProfileBootstrapUser)) {
    $resolvedBootstrapUserId = $firstLaunchProfileBootstrapUser
    $resolvedBootstrapSource = "launch profile value ($LaunchProfile in launchSettings.json)"
  }

  $trimmedFallbackBootstrapUserId =
    if ([string]::IsNullOrWhiteSpace($BootstrapUserIdFallback)) { "" } else { $BootstrapUserIdFallback.Trim() }

  if ([string]::IsNullOrWhiteSpace($resolvedBootstrapUserId) -and -not [string]::IsNullOrWhiteSpace($trimmedFallbackBootstrapUserId)) {
    $resolvedBootstrapUserId = $trimmedFallbackBootstrapUserId
    $resolvedBootstrapSource = "fallback default (-BootstrapUserIdFallback)"
  }

  if ([string]::IsNullOrWhiteSpace($resolvedBootstrapUserId)) {
    throw "Could not resolve a bootstrap user ID. Pass -BootstrapUserId explicitly."
  }

  $effectiveBootstrapUsers = Merge-UserIds @($existingBootstrapUsers, $resolvedBootstrapUserId)
  if ([string]::IsNullOrWhiteSpace($effectiveBootstrapUsers)) {
    throw "Could not compute effective bootstrap users."
  }

  $env:grace__authz__bootstrap__system_admin_users = $effectiveBootstrapUsers

  $graceTestingValue = $env:GRACE_TESTING
  $graceTestingSource =
    if ([string]::IsNullOrWhiteSpace($graceTestingValue)) {
      $env:GRACE_TESTING = "1"
      "script defaulted to 1 for TestAuth"
    } else {
      "inherited from shell"
    }

  Write-Detail "Resolved bootstrap user ID: $resolvedBootstrapUserId" "Green"
  Write-Detail "Resolved bootstrap source: $resolvedBootstrapSource" "Green"
  Write-Detail "Effective bootstrap users env value: $effectiveBootstrapUsers" "Green"
  Write-Detail "Launch profile bootstrap users value: $(if ([string]::IsNullOrWhiteSpace($launchProfileBootstrapUsers)) { '<none>' } else { $launchProfileBootstrapUsers })"
  Write-Detail "GRACE_TESTING: $($env:GRACE_TESTING) ($graceTestingSource)"

  $serverAlreadyRunning = Test-GraceHealth -HealthUrl $healthUrl
  if ($serverAlreadyRunning) {
    Write-Step "Grace.Server is already healthy; skipping AppHost startup." "Green"
  } else {
    $staleProcesses = Get-DebugLocalDotnetProcesses -ProjectPath $resolvedProjectPath -LaunchProfile $LaunchProfile
    if ($staleProcesses.Count -gt 0) {
      Write-Step "Detected $($staleProcesses.Count) stale DebugLocal AppHost process(es); cleaning up before startup." "Yellow"
      foreach ($staleProcess in $staleProcesses) {
        Write-Detail "Stale process PID $($staleProcess.ProcessId): $($staleProcess.CommandLine)" "Yellow"
        $cleanupResult = Stop-ProcessWithDiagnostics -ProcessId $staleProcess.ProcessId -WaitSeconds $CleanupWaitSeconds -Context "stale-process-cleanup"
        $note = "stale-process-cleanup pid=$($cleanupResult.ProcessId) success=$($cleanupResult.Success) message='$($cleanupResult.Message)'"
        $cleanupNotes.Add($note)
        Write-Detail $note ($(if ($cleanupResult.Success) { "Green" } else { "Red" }))
      }
    }

    Write-Step "Starting Grace.Aspire.AppHost in the background." "Green"

    $stdoutLog = Join-Path $logRoot "start-debuglocal-$runId.stdout.log"
    $stderrLog = Join-Path $logRoot "start-debuglocal-$runId.stderr.log"

    $dotnetArgs = @("run", "--project", $resolvedProjectPath, "--launch-profile", $LaunchProfile)
    if ($NoBuild) {
      $dotnetArgs += "--no-build"
    }

    Write-Detail "AppHost stdout log: $stdoutLog"
    Write-Detail "AppHost stderr log: $stderrLog"
    Write-Detail "Command: dotnet $($dotnetArgs -join ' ')"

    $appHostProcess = Start-Process `
      -FilePath "dotnet" `
      -ArgumentList $dotnetArgs `
      -WorkingDirectory $repoRoot `
      -RedirectStandardOutput $stdoutLog `
      -RedirectStandardError $stderrLog `
      -PassThru

    $startedAppHostThisRun = $true
    Write-Detail "AppHost PID: $($appHostProcess.Id)"

    Write-Step "Waiting for Grace.Server health endpoint."
    $healthy = Wait-ForGraceServer -HealthUrl $healthUrl -TimeoutSeconds $StartupTimeoutSeconds -DelayMilliseconds 1000 -AppHostProcess $appHostProcess

    if (-not $healthy) {
      if ($null -ne $appHostProcess -and $appHostProcess.HasExited) {
        $message = "AppHost exited early with code $($appHostProcess.ExitCode)."
        Write-Detail $message "Red"
        Set-LastFailureInfo -FailureInfo (New-FailureInfo -Stage "server startup" -Classification "configuration" -StatusCode $null -Retryable $false -Message $message -Hint "Inspect AppHost logs for startup errors and launch profile configuration.")
      } else {
        $message = "Grace.Server health check timed out after $StartupTimeoutSeconds second(s)."
        Write-Detail $message "Red"
        Set-LastFailureInfo -FailureInfo (New-FailureInfo -Stage "server startup" -Classification "transient" -StatusCode $null -Retryable $true -Message $message -Hint "The runtime did not reach healthy state in time. Check AppHost logs and consider increasing -StartupTimeoutSeconds.")
      }

      throw "Grace.Server did not become healthy at $healthUrl."
    }

    Write-Detail "Grace.Server is healthy." "Green"
  }

  if (-not $SkipAuthProbe) {
    Invoke-AuthProbe -GraceServerUri $GraceServerUri -BootstrapUserId $resolvedBootstrapUserId
  } else {
    Write-Step "Auth readiness probe skipped by request (-SkipAuthProbe)." "Yellow"
  }

  if (-not $NoTokenBootstrap) {
    Write-Step "Creating a personal access token using TestAuth context." "Green"

    $requestedTokenName =
      if ([string]::IsNullOrWhiteSpace($TokenName)) {
        "local-dev"
      } else {
        $TokenName.Trim()
      }

    $expiresInSeconds = [int64]($TokenDays * 86400L)
    $requestBody = @{
      TokenName        = $requestedTokenName
      ExpiresInSeconds = $expiresInSeconds
      NoExpiry         = $false
    } | ConvertTo-Json

    Write-Detail "Token user ID: $resolvedBootstrapUserId"
    Write-Detail "Token name: $requestedTokenName"
    Write-Detail "Token lifetime (days): $TokenDays"

    $tokenBootstrapResult = Invoke-TokenBootstrapWithRetry `
      -GraceServerUri $GraceServerUri `
      -BootstrapUserId $resolvedBootstrapUserId `
      -RequestBodyJson $requestBody `
      -MaxAttempts $TokenBootstrapMaxAttempts `
      -InitialBackoffSeconds $TokenBootstrapInitialBackoffSeconds

    Write-Detail "Token bootstrap completed in $($tokenBootstrapResult.Attempts) attempt(s), $($tokenBootstrapResult.ElapsedSeconds)s total." "Green"

    $response = $tokenBootstrapResult.Response
    $token = $null
    if ($null -ne $response.ReturnValue -and $null -ne $response.ReturnValue.Token) {
      $token = [string]$response.ReturnValue.Token
    } elseif ($null -ne $response.Token) {
      $token = [string]$response.Token
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
      Set-LastFailureInfo -FailureInfo (
        New-FailureInfo `
          -Stage "token bootstrap" `
          -Classification "unknown" `
          -StatusCode $null `
          -Retryable $false `
          -Message "Token endpoint returned success, but token value was empty." `
          -Hint "Verify auth response shape and inspect server logs for serialization or contract errors."
      )
      throw "Token creation succeeded but no token was returned."
    }

    $env:GRACE_SERVER_URI = $GraceServerUri
    $env:GRACE_TOKEN = $token

    Write-Detail "Set GRACE_SERVER_URI and GRACE_TOKEN for this shell." "Green"

    Write-Host ""
    Write-Host "Copy/paste (PowerShell):" -ForegroundColor Cyan
    Write-Host "  `$env:GRACE_SERVER_URI = `"$GraceServerUri`""
    Write-Host "  `$env:GRACE_TOKEN      = `"$token`""
    Write-Host ""
    Write-Host "Copy/paste (bash/zsh):" -ForegroundColor Cyan
    Write-Host "  export GRACE_SERVER_URI=`"$GraceServerUri`""
    Write-Host "  export GRACE_TOKEN=`"$token`""
    Write-Host ""
  } else {
    Write-Step "Token bootstrap skipped by request (-NoTokenBootstrap)." "Yellow"
  }

  Write-Step "DebugLocal startup workflow is complete." "Green"
  Write-Detail "Bootstrap user ID: $resolvedBootstrapUserId"
  Write-Detail "Bootstrap source: $resolvedBootstrapSource"
  Write-Detail "Grace server URI: $GraceServerUri"

  if ($null -ne $appHostProcess) {
    Write-Detail "AppHost PID: $($appHostProcess.Id)"
    Write-Detail "Stop command: Stop-Process -Id $($appHostProcess.Id)" "Yellow"
  }

  if (-not [string]::IsNullOrWhiteSpace($stdoutLog)) {
    Write-Detail "AppHost stdout log: $stdoutLog"
  }

  if (-not [string]::IsNullOrWhiteSpace($stderrLog)) {
    Write-Detail "AppHost stderr log: $stderrLog"
  }
} catch {
  $capturedError = $_
  $failed = $true
} finally {
  if ($failed) {
    if ($null -ne $appHostProcess -and $startedAppHostThisRun) {
      Write-Step "Running failure-path cleanup for AppHost process." "Yellow"
      $cleanupResult = Stop-ProcessWithDiagnostics -ProcessId $appHostProcess.Id -WaitSeconds $CleanupWaitSeconds -Context "failure-cleanup"
      $note = "failure-cleanup pid=$($cleanupResult.ProcessId) success=$($cleanupResult.Success) message='$($cleanupResult.Message)'"
      $cleanupNotes.Add($note)
      Write-Detail $note ($(if ($cleanupResult.Success) { "Green" } else { "Red" }))
    } elseif ($null -ne $appHostProcess) {
      $note = "failure-cleanup skipped for pid=$($appHostProcess.Id) because process was not started by this run."
      $cleanupNotes.Add($note)
      Write-Detail $note "Yellow"
    } else {
      $note = "failure-cleanup skipped because no AppHost process was created."
      $cleanupNotes.Add($note)
      Write-Detail $note "Yellow"
    }

    Write-FailureDiagnostics `
      -RepoRoot $repoRoot `
      -ScriptDirectory $scriptDir `
      -LogRoot $logRoot `
      -RunId $runId `
      -GraceServerUri $GraceServerUri `
      -HealthUrl $healthUrl `
      -BootstrapUserId $resolvedBootstrapUserId `
      -StdoutLog $stdoutLog `
      -StderrLog $stderrLog `
      -CleanupNotes $cleanupNotes `
      -ErrorRecord $capturedError

    if ($null -ne $capturedError) {
      throw $capturedError
    }

    throw "DebugLocal startup failed."
  }
}
