#!/usr/bin/env pwsh
<#
  Compatibility wrapper for local onboarding.

  Canonical script:
    pwsh ./scripts/start-debuglocal.ps1

  Run from repo root:
    pwsh ./scripts/dev-local.ps1
#>

[CmdletBinding()]
param(
  [string] $LaunchProfile = "DebugLocal",
  [string] $GraceServerUri = "http://localhost:5000",
  [string] $TestUserId = "",
  [string] $BootstrapUserId = "",
  [string] $TokenName = "local-dev",
  [int]    $TokenDays = 30,
  [int]    $StartupTimeoutSeconds = 240,
  [switch] $NoBuild,
  [switch] $NoTokenBootstrap
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$startDebugLocalScript = Join-Path $scriptDir "start-debuglocal.ps1"

if (-not (Test-Path $startDebugLocalScript)) {
  throw "Expected canonical script '$startDebugLocalScript' was not found."
}

$resolvedBootstrapUserId =
  if (-not [string]::IsNullOrWhiteSpace($BootstrapUserId)) {
    $BootstrapUserId.Trim()
  } elseif (-not [string]::IsNullOrWhiteSpace($TestUserId)) {
    $TestUserId.Trim()
  } else {
    ""
  }

$forwardedParameters = @{
  LaunchProfile          = $LaunchProfile
  GraceServerUri         = $GraceServerUri
  TokenName              = $TokenName
  TokenDays              = $TokenDays
  StartupTimeoutSeconds  = $StartupTimeoutSeconds
  NoBuild                = $NoBuild
  NoTokenBootstrap       = $NoTokenBootstrap
}

if (-not [string]::IsNullOrWhiteSpace($resolvedBootstrapUserId)) {
  $forwardedParameters["BootstrapUserId"] = $resolvedBootstrapUserId
}

Write-Host ""
Write-Host "dev-local.ps1 is a compatibility alias." -ForegroundColor Yellow
Write-Host "Using canonical script: ./scripts/start-debuglocal.ps1" -ForegroundColor Yellow
Write-Host ""

& $startDebugLocalScript @forwardedParameters
