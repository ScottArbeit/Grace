[CmdletBinding()]
param(
    [switch]$SkipDocker,
    [switch]$CI
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($CI) {
    $ProgressPreference = "SilentlyContinue"
}

$startTime = Get-Date
$exitCode = 0

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host ("== {0} ==" -f $Title)
}

function Invoke-External([string]$Label, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

try {
    Write-Section "Prerequisites"

    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw "PowerShell 7.x is required. Current version: $($PSVersionTable.PSVersion)."
    }

    Write-Verbose ("PowerShell version: {0}" -f $PSVersionTable.PSVersion)

    try {
        Get-Command dotnet -ErrorAction Stop | Out-Null
    } catch {
        throw "dotnet SDK not found. Install the .NET 10 SDK and retry."
    }

    $dotnetVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet --version failed. Ensure the .NET 10 SDK is installed."
    }
    if ([string]::IsNullOrWhiteSpace($dotnetVersion)) {
        throw "dotnet --version returned an empty value."
    }

    $dotnetMajor = [int]($dotnetVersion.Split(".")[0])
    if ($dotnetMajor -lt 10) {
        throw "dotnet SDK 10.x required. Detected $dotnetVersion."
    }

    if (-not $SkipDocker) {
        try {
            Get-Command docker -ErrorAction Stop | Out-Null
        } catch {
            throw "Docker is required for full validation. Install Docker Desktop or run with -SkipDocker."
        }

        Invoke-External "docker info" { docker info | Out-Null }
    } else {
        Write-Host "Skipping Docker check (-SkipDocker)."
    }

    Write-Section "Restore Tools"
    Invoke-External "dotnet tool restore" { dotnet tool restore }

    Write-Section "Restore Packages"
    Invoke-External "dotnet restore" { dotnet restore "src/Grace.sln" }

    Write-Section "Next Steps"
    Write-Host "Run: pwsh ./scripts/validate.ps1 -Fast"
    Write-Host "Use -Full for Aspire integration coverage."
} catch {
    $exitCode = 1
    Write-Error $_
} finally {
    $elapsed = (Get-Date) - $startTime
    Write-Host ""
    Write-Host ("Elapsed: {0:c}" -f $elapsed)

    if ($exitCode -ne 0) {
        exit $exitCode
    }
}
