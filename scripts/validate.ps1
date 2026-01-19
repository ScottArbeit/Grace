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
$formatDisabled = $true

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host ("== {0} ==" -f $Title)
}

function Get-FormatTargets {
    $targets = @()
    $git = Get-Command git -ErrorAction SilentlyContinue
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $prefix = "src{0}" -f $separator
    $isCi = $env:GITHUB_ACTIONS -eq "true" -or $env:CI -eq "true"

    if ($isCi -and $null -ne $git) {
        $diffPaths = @()
        $event = $null

        if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_EVENT_PATH) -and (Test-Path $env:GITHUB_EVENT_PATH)) {
            try {
                $event = Get-Content $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
            } catch {
                $event = $null
            }
        }

        if ($env:GITHUB_EVENT_NAME -like "pull_request*") {
            $baseSha =
                if ($null -ne $event -and $null -ne $event.pull_request) { $event.pull_request.base.sha } else { $null }

            if ([string]::IsNullOrWhiteSpace($baseSha) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_BASE_SHA)) {
                $baseSha = $env:GITHUB_BASE_SHA
            }

            if (-not [string]::IsNullOrWhiteSpace($baseSha)) {
                $null = & git fetch --no-tags --depth=1 origin $baseSha 2>$null
                $diffPaths = & git diff --name-only $baseSha HEAD 2>$null
                if ($LASTEXITCODE -ne 0) {
                    $diffPaths = @()
                }
            }
        } elseif ($env:GITHUB_EVENT_NAME -eq "push") {
            $baseSha = if ($null -ne $event) { $event.before } else { $null }
            $headSha = if ($null -ne $event) { $event.after } else { $null }

            if ([string]::IsNullOrWhiteSpace($baseSha) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_EVENT_BEFORE)) {
                $baseSha = $env:GITHUB_EVENT_BEFORE
            }

            if ([string]::IsNullOrWhiteSpace($headSha) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
                $headSha = $env:GITHUB_SHA
            }

            if (-not [string]::IsNullOrWhiteSpace($baseSha) -and -not [string]::IsNullOrWhiteSpace($headSha)) {
                $null = & git fetch --no-tags --depth=1 origin $baseSha $headSha 2>$null
                $diffPaths = & git diff --name-only $baseSha $headSha 2>$null
                if ($LASTEXITCODE -ne 0) {
                    $diffPaths = @()
                }
            }
        }

        if (-not $diffPaths -or $diffPaths.Count -eq 0) {
            $diffPaths = & git diff --name-only HEAD~1 HEAD 2>$null
            if ($LASTEXITCODE -ne 0) {
                $diffPaths = @()
            }
        }

        foreach ($path in $diffPaths) {
            if ([string]::IsNullOrWhiteSpace($path)) {
                continue
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

function Invoke-ParallelTests([array]$TestRuns, [string]$Configuration, [string]$RepoRoot) {
    if (-not $TestRuns -or $TestRuns.Count -eq 0) {
        return
    }

    $jobs = foreach ($testRun in $TestRuns) {
        Start-Job -Name $testRun.Label -ArgumentList $testRun.Project, $Configuration, $RepoRoot, $testRun.Label -ScriptBlock {
            param($project, $configuration, $repoRoot, $label)
            Set-Location $repoRoot
            dotnet test $project -c $configuration
            $exitCode = $LASTEXITCODE
            [pscustomobject]@{ Label = $label; ExitCode = $exitCode }
        }
    }

    $results = @()
    foreach ($job in $jobs) {
        $jobOutput = Receive-Job -Job $job -Wait -AutoRemoveJob
        foreach ($item in $jobOutput) {
            if ($item -is [pscustomobject] -and $item.PSObject.Properties.Name -contains "ExitCode") {
                $results += $item
            } else {
                Write-Host $item
            }
        }
    }

    $failed = $results | Where-Object { $_.ExitCode -ne 0 }
    if ($failed) {
        $failedLabels = ($failed | Select-Object -ExpandProperty Label) -join ", "
        throw "Tests failed: $failedLabels."
    }
}

try {
    if (-not $Fast -and -not $Full) {
        $Fast = $true
    }

    if ($Fast -and $Full) {
        throw "Choose either -Fast or -Full, not both."
    }

    if ($formatDisabled) {
        Write-Section "Format"
        Write-Host "Skipped (temporarily disabled pending full repo formatting)."
    } elseif (-not $SkipFormat) {
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
        if ($Full) {
            $repoRoot = (Get-Location).Path
            $testRuns = @(
                [pscustomobject]@{
                    Label = "Grace.CLI.Tests"
                    Project = "src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj"
                },
                [pscustomobject]@{
                    Label = "Grace.Server.Tests"
                    Project = "src/Grace.Server.Tests/Grace.Server.Tests.fsproj"
                }
            )

            Invoke-ParallelTests -TestRuns $testRuns -Configuration $Configuration -RepoRoot $repoRoot
        } else {
            Invoke-External "Grace.CLI.Tests" { dotnet test "src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj" -c $Configuration }
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
