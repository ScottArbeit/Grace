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

function Get-GraceBuildProperties {
    $properties = @()

    if (-not [string]::IsNullOrWhiteSpace($env:GraceBuildKind)) {
        $properties += "-p:GraceBuildKind=$env:GraceBuildKind"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GraceBuildNumber)) {
        $properties += "-p:GraceBuildNumber=$env:GraceBuildNumber"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GraceSourceRevisionId)) {
        $properties += "-p:GraceSourceRevisionId=$env:GraceSourceRevisionId"
    }

    [string[]]$properties
}

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

function Join-TestFilter([string[]]$Terms) {
    if ($null -eq $Terms -or $Terms.Length -eq 0) {
        throw "At least one test filter term is required."
    }

    $Terms -join "|"
}

function Get-FSharpProjectTypeFilterTerms([string]$ProjectPath) {
    if (-not (Test-Path $ProjectPath)) {
        throw "Test project '$ProjectPath' was not found."
    }

    $projectDirectory = Split-Path -Path $ProjectPath -Parent
    [xml]$project = Get-Content -Path $ProjectPath -Raw
    $terms = @()

    foreach ($compileItem in $project.SelectNodes("//Compile")) {
        $include = $compileItem.Include
        if ([string]::IsNullOrWhiteSpace($include)) {
            continue
        }

        $sourcePath = Join-Path $projectDirectory $include
        if (-not (Test-Path $sourcePath)) {
            throw "Compile item '$include' from '$ProjectPath' was not found."
        }

        $namespace = $null
        foreach ($line in Get-Content -Path $sourcePath) {
            if ($line -match '^\s*namespace\s+([A-Za-z0-9_.]+)') {
                $namespace = $Matches[1]
                continue
            }

            if ($line -match '^\s*type\s+(?!private\b)([A-Za-z_][A-Za-z0-9_]*)') {
                if ([string]::IsNullOrWhiteSpace($namespace)) {
                    continue
                }

                $terms += "FullyQualifiedName~$namespace.$($Matches[1])"
            }
        }
    }

    [string[]]($terms | Select-Object -Unique)
}

function Get-ServerUnitTestFilterTerms {
    $terms = Get-FSharpProjectTypeFilterTerms "src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj"
    if ($terms.Length -eq 0) {
        throw "No server unit test filter terms were discovered."
    }

    [string[]]$terms
}

function Get-FastTestFilterTerms {
    $terms = @(
        "FullyQualifiedName~Grace.Authorization.Tests",
        "FullyQualifiedName~Grace.CLI.Tests",
        "FullyQualifiedName~Grace.Operations.Tests",
        "FullyQualifiedName~Grace.Types.Tests"
    )

    [string[]]($terms + (Get-ServerUnitTestFilterTerms))
}

function Get-TestFilter([bool]$IncludeFullTests) {
    $terms = Get-FastTestFilterTerms

    if ($IncludeFullTests) {
        $terms += "FullyQualifiedName~Grace.Server.Tests"
    }

    Join-TestFilter $terms
}

function Invoke-SolutionTests([string]$Configuration, [bool]$IncludeFullTests) {
    $testFilter = Get-TestFilter $IncludeFullTests

    Write-Host "Test target: src/Grace.slnx"
    Write-Host ("Test filter: {0}" -f $testFilter)
    if ($IncludeFullTests) {
        Write-Host "Progress note: -Full includes Grace.Server.Tests; the Aspire-backed test assembly generally runs for 3-4 minutes after the faster assemblies finish."
        Write-Host "Progress note: VSTest may buffer test-host progress output; wait for the Grace.Server.Tests summary before treating quiet output as a hang."
    }

    $previousServerCleanup = $env:GRACE_TEST_SERVER_CLEANUP
    $setServerCleanupForRun = $IncludeFullTests -and [string]::IsNullOrWhiteSpace($env:GRACE_TEST_SERVER_CLEANUP)

    if ($setServerCleanupForRun) {
        $env:GRACE_TEST_SERVER_CLEANUP = "0"
        Write-Host "Server-side test cleanup: skipped for this validate.ps1 -Full run. Set GRACE_TEST_SERVER_CLEANUP=1 to enable it."
    }

    try {
        Write-Host ("Running: dotnet test `"src/Grace.slnx`" -c {0} --no-build --filter `"{1}`"" -f $Configuration, $testFilter)

        Invoke-External "Grace solution tests" {
            dotnet test "src/Grace.slnx" -c $Configuration --no-build --filter $testFilter
        }
    } finally {
        if ($setServerCleanupForRun) {
            if ($null -eq $previousServerCleanup) {
                Remove-Item Env:\GRACE_TEST_SERVER_CLEANUP -ErrorAction SilentlyContinue
            } else {
                $env:GRACE_TEST_SERVER_CLEANUP = $previousServerCleanup
            }
        }
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
#        Write-Section "Format"
#        Write-Host "Skipped (temporarily disabled pending full repo formatting)."
    } elseif (-not $SkipFormat) {
        Write-Section "Format"
        #Invoke-External "dotnet tool restore" { dotnet tool restore }

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

    $graceBuildProperties = @(Get-GraceBuildProperties)

    if (-not $SkipBuild) {
        Write-Section "Build"
        Write-Host "Build properties: " -ForegroundColor Green -NoNewline
        if ($graceBuildProperties.Count -eq 0) {
            Write-Host "(none)" -ForegroundColor DarkYellow -NoNewline
        } else {
            $graceBuildProperties | ForEach-Object {
                Write-Host "$_; " -ForegroundColor DarkYellow -NoNewline
            }
        }
        Write-Host ""
        Invoke-External "Grace solution build" { dotnet build "src/Grace.slnx" -c $Configuration @graceBuildProperties }
    } else {
        Write-Section "Build"
        Write-Host "Skipped (-SkipBuild)."
    }

    if (-not $SkipTests) {
        Write-Section "Test"
        Invoke-SolutionTests $Configuration $Full
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
