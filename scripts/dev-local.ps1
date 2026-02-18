#!/usr/bin/env pwsh
<#
  Starts Grace via Aspire DebugLocal, waits for grace-server, creates a PAT using TestAuth,
  and prints copy-pastable environment variable commands plus key local endpoints.

  Run from repo root:
    pwsh ./scripts/dev-local.ps1
#>

[CmdletBinding()]
param(
  [string] $LaunchProfile = "DebugLocal",
  [string] $GraceServerUri = "http://localhost:5000",
  [string] $TestUserId = "test-admin",
  [string] $TokenName = "local-dev",
  [int]    $TokenDays = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Resolve-Path (Join-Path $scriptDir "..")
Set-Location $repoRoot

$logRoot = Join-Path $repoRoot ".grace" | Join-Path -ChildPath "logs"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

$stdoutLog = Join-Path $logRoot "aspire-apphost.stdout.log"
$stderrLog = Join-Path $logRoot "aspire-apphost.stderr.log"

Write-Host ""
Write-Host "Starting Aspire AppHost (launch profile: $LaunchProfile)..." -ForegroundColor Cyan
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"
Write-Host ""

# Start Aspire AppHost in the background (so we can continue to create a token).
$proj = Join-Path $repoRoot "src/Grace.Aspire.AppHost/Grace.Aspire.AppHost.csproj"

$proc = Start-Process `
  -FilePath "dotnet" `
  -ArgumentList @("run","--project",$proj,"--launch-profile",$LaunchProfile) `
  -WorkingDirectory $repoRoot `
  -RedirectStandardOutput $stdoutLog `
  -RedirectStandardError  $stderrLog `
  -PassThru

Write-Host "Aspire AppHost PID: $($proc.Id)" -ForegroundColor DarkGray

# Wait for grace-server to come up.
Write-Host ""
Write-Host "Waiting for Grace.Server at $GraceServerUri ..." -ForegroundColor Cyan

$healthUrl = "$GraceServerUri/healthz"
$maxAttempts = 240
$delayMs = 1000

$healthy = $false
for ($i = 1; $i -le $maxAttempts; $i++) {
  try {
    $r = Invoke-WebRequest -Uri $healthUrl -Method GET -TimeoutSec 2
    if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300) {
      $healthy = $true
      break
    }
  } catch {
    Start-Sleep -Milliseconds $delayMs
  }
}

if (-not $healthy) {
  Write-Host ""
  Write-Host "Grace.Server did not become healthy at $healthUrl." -ForegroundColor Red
  Write-Host "Check logs:" -ForegroundColor Red
  Write-Host "  $stdoutLog" -ForegroundColor Red
  Write-Host "  $stderrLog" -ForegroundColor Red
  Write-Host ""
  Write-Host "To stop Aspire AppHost:" -ForegroundColor Yellow
  Write-Host "  Stop-Process -Id $($proc.Id)" -ForegroundColor Yellow
  exit 1
}

Write-Host "Grace.Server is up." -ForegroundColor Green

# Create a PAT using TestAuth (x-grace-user-id).
$expiresInSeconds = [int64]($TokenDays * 86400)

$body = @{
  TokenName        = $TokenName
  ExpiresInSeconds = $expiresInSeconds
  NoExpiry         = $false
} | ConvertTo-Json

$headers = @{
  "x-grace-user-id" = $TestUserId
}

Write-Host ""
Write-Host "Creating a Personal Access Token for '$TestUserId'..." -ForegroundColor Cyan

$resp = Invoke-RestMethod `
  -Method Post `
  -Uri "$GraceServerUri/auth/token/create" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body

$token = $resp.ReturnValue.Token
if ([string]::IsNullOrWhiteSpace($token)) {
  throw "Token creation succeeded but no token was returned. Response: $($resp | ConvertTo-Json -Depth 8)"
}

Write-Host ""
Write-Host "✅ Local credentials" -ForegroundColor Green
Write-Host "  GRACE_SERVER_URI: $GraceServerUri"
Write-Host "  GRACE_TOKEN     : $token"
Write-Host ""

Write-Host "Copy/paste (PowerShell):" -ForegroundColor Cyan
Write-Host "  `$env:GRACE_SERVER_URI = `"$GraceServerUri`""
Write-Host "  `$env:GRACE_TOKEN      = `"$token`""
Write-Host ""

Write-Host "Copy/paste (bash/zsh):" -ForegroundColor Cyan
Write-Host "  export GRACE_SERVER_URI=`"$GraceServerUri`""
Write-Host "  export GRACE_TOKEN=`"$token`""
Write-Host ""

Write-Host "✅ Local endpoints (common defaults)" -ForegroundColor Green
Write-Host "  Grace.Server HTTP : http://localhost:5000"
Write-Host "  Grace.Server HTTPS: https://localhost:5001"
Write-Host "  Aspire dashboard  : http://localhost:18888"
Write-Host "  Azurite (Blob)    : http://localhost:10000"
Write-Host "  Azurite (Queue)   : http://localhost:10001"
Write-Host "  Azurite (Table)   : http://localhost:10002"
Write-Host "  Cosmos emulator   : https://localhost:8081"
Write-Host "  Service Bus AMQP   : amqp://localhost:5672"
Write-Host "  Service Bus mgmt   : http://localhost:5300"
Write-Host "  Redis              : localhost:6379"
Write-Host ""

Write-Host "To stop Aspire AppHost:" -ForegroundColor Yellow
Write-Host "  Stop-Process -Id $($proc.Id)" -ForegroundColor Yellow
Write-Host ""
