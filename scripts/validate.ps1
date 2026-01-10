[CmdletBinding()]
param(
    [switch]$Fast,
    [switch]$Full,
    [switch]$SkipFormat,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$startTime = Get-Date
$exitCode = 0

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host ("== {0} ==" -f $Title)
}

function Get-FormatTargets {
    $targets = @()
    $git = Get-Command git -ErrorAction SilentlyContinue
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $prefix = "src{0}" -f $separator
    $root = (Get-Location).Path
    $isCi = $env:GITHUB_ACTIONS -eq "true" -or $env:CI -eq "true"

    if ($isCi) {
        $targets =
            Get-ChildItem -Path "src" -Recurse -File
            | Where-Object {
                $extension = $_.Extension.ToLowerInvariant()
                $extension -in @(".fs", ".fsi", ".fsx") -and
                $_.FullName -notmatch "[\\/](bin|obj)[\\/]"
            }
            | ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName) }

        return [string[]]($targets | Select-Object -Unique)
    }

    if ($null -ne $git) {
        $statusLines = & git status --porcelain
        foreach ($line in $statusLines) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $path = $line.Substring(3)
            if ($path -match " -> ") {
                $path = $path.Split(" -> ")[-1]
            }

            $path = $path -replace "[\\/]", $separator
            if (-not $path.StartsWith($prefix)) {
                continue
            }

            $extension = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
            if ($extension -in @(".fs", ".fsi", ".fsx")) {
                $targets += $path
            }
        }
    }

    [string[]]($targets | Select-Object -Unique)
}

function Invoke-External([string]$Label, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

try {
    if (-not $Fast -and -not $Full) {
        $Fast = $true
    }

    if ($Fast -and $Full) {
        throw "Choose either -Fast or -Full, not both."
    }

    if (-not $SkipFormat) {
        Write-Section "Format"
        Invoke-External "dotnet tool restore" { dotnet tool restore }

        $formatTargets = Get-FormatTargets
        if (-not $formatTargets -or $formatTargets.Length -eq 0) {
            Write-Host "No changed F# files detected. Skipping format check."
        } else {
            $separator = [System.IO.Path]::DirectorySeparatorChar
            $prefix = "src{0}" -f $separator
            $relativeTargets =
                $formatTargets
                | ForEach-Object { $_.Substring($prefix.Length) }

            Push-Location "src"
            try {
                & dotnet tool run fantomas --check @relativeTargets
                if ($LASTEXITCODE -ne 0) {
                    if ($LASTEXITCODE -eq 1) {
                        throw "Formatting drift detected. Run 'dotnet tool run fantomas --recurse .' from ./src to apply formatting."
                    }

                    throw "Fantomas failed with exit code $LASTEXITCODE."
                }
            } finally {
                Pop-Location
            }
        }
    } else {
        Write-Section "Format"
        Write-Host "Skipped (-SkipFormat)."
    }

    if (-not $SkipBuild) {
        Write-Section "Build"
        Invoke-External "Grace.Server build" { dotnet build "src/Grace.Server/Grace.Server.fsproj" -c $Configuration }
        Invoke-External "Grace.CLI build" { dotnet build "src/Grace.CLI/Grace.CLI.fsproj" -c $Configuration }
        Invoke-External "Grace.SDK build" { dotnet build "src/Grace.SDK/Grace.SDK.fsproj" -c $Configuration }
    } else {
        Write-Section "Build"
        Write-Host "Skipped (-SkipBuild)."
    }

    if (-not $SkipTests) {
        Write-Section "Test"
        Invoke-External "Grace.CLI.Tests" { dotnet test "src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj" -c $Configuration --no-build }

        if ($Full) {
            Invoke-External "Grace.Server.Tests" { dotnet test "src/Grace.Server.Tests/Grace.Server.Tests.fsproj" -c $Configuration --no-build }
        }
    } else {
        Write-Section "Test"
        Write-Host "Skipped (-SkipTests)."
    }
} catch {
    $exitCode = 1
    $message =
        if ($null -ne $_.Exception -and -not [string]::IsNullOrWhiteSpace($_.Exception.Message)) {
            $_.Exception.Message
        } else {
            $_.ToString()
        }

    Write-Error ("Validation failed: {0}" -f $message)
} finally {
    $elapsed = (Get-Date) - $startTime
    Write-Host ""
    Write-Host ("Elapsed: {0:c}" -f $elapsed)

    if ($exitCode -ne 0) {
        exit $exitCode
    }
}
