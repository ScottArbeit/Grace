[CmdletBinding()]
param(
    [string] $GraceServerUri = "http://localhost:5000",
    [ValidateRange(30, 1800)]
    [int] $StartupTimeoutSeconds = 300,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",
    [switch] $PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bootstrapModeVariable = "GRACE_DEBUGAZURE_BOOTSTRAP_MODE"
$bootstrapUserIdVariable = "GRACE_DEBUGAZURE_BOOTSTRAP_USER_ID"
$repoRoot = Split-Path -Parent $PSScriptRoot
$appHostProject = Join-Path $repoRoot "src\Grace.Aspire.AppHost\Grace.Aspire.AppHost.csproj"
$cliProject = Join-Path $repoRoot "src\Grace.CLI\Grace.CLI.fsproj"
$cliPath = Join-Path $repoRoot "src\Grace.CLI\bin\$Configuration\net10.0\grace.exe"
$logDirectory = Join-Path $repoRoot ".grace\logs"
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$stdoutPath = Join-Path $logDirectory "start-debugazure-$runId.stdout.log"
$stderrPath = Join-Path $logDirectory "start-debugazure-$runId.stderr.log"

function Write-Status {
    param([Parameter(Mandatory)][string] $Message)

    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message"
}

function Assert-GraceServerUriAvailable {
    <#
    .SYNOPSIS
    Rejects a pre-existing Grace Server listener so readiness cannot be satisfied by an older DebugAzure child.
    #>
    $serverUri = $null
    if (-not [Uri]::TryCreate($GraceServerUri, [UriKind]::Absolute, [ref] $serverUri) -or
        $serverUri.Scheme -notin @('http', 'https')) {
        throw "GraceServerUri must be an absolute HTTP or HTTPS URI."
    }

    $tcpClient = [Net.Sockets.TcpClient]::new()
    $listenerDetected = $false
    try {
        $tcpClient.ConnectAsync($serverUri.Host, $serverUri.Port).WaitAsync([TimeSpan]::FromSeconds(1)).GetAwaiter().GetResult()
        $listenerDetected = $true
    }
    catch [Net.Sockets.SocketException] {
        $listenerDetected = $false
    }
    catch [TimeoutException] {
        $listenerDetected = $false
    }
    finally {
        $tcpClient.Dispose()
    }

    if ($listenerDetected) {
        throw "Grace Server URI '$GraceServerUri' already has a listener. Stop the existing DebugAzure process before deployment."
    }
}

function Stop-AppHostProcess {
    param([Parameter(Mandatory)][System.Diagnostics.Process] $Process)

    if ($Process.HasExited) {
        return
    }

    Write-Status "Stopping Aspire AppHost process tree (PID $($Process.Id))."
    $Process.Kill($true)
    $Process.WaitForExit()
}

function Start-AppHostProcess {
    param([AllowNull()][string] $BootstrapUserId)

    $originalBootstrapMode = [Environment]::GetEnvironmentVariable($bootstrapModeVariable, "Process")
    $originalBootstrapUserId = [Environment]::GetEnvironmentVariable($bootstrapUserIdVariable, "Process")

    try {
        if ([string]::IsNullOrWhiteSpace($BootstrapUserId)) {
            [Environment]::SetEnvironmentVariable($bootstrapModeVariable, "Suppress", "Process")
            [Environment]::SetEnvironmentVariable($bootstrapUserIdVariable, $null, "Process")
            Write-Status "Starting DebugAzure without bootstrap authorization."
        }
        else {
            [Environment]::SetEnvironmentVariable($bootstrapModeVariable, "ExactUser", "Process")
            [Environment]::SetEnvironmentVariable($bootstrapUserIdVariable, $BootstrapUserId, "Process")
            Write-Status "Restarting DebugAzure with the authenticated user as the one-time bootstrap candidate."
        }

        return Start-Process `
            -FilePath "dotnet" `
            -ArgumentList @("run", "--project", $appHostProject, "--launch-profile", "DebugAzure") `
            -WorkingDirectory $repoRoot `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -PassThru
    }
    finally {
        [Environment]::SetEnvironmentVariable($bootstrapModeVariable, $originalBootstrapMode, "Process")
        [Environment]::SetEnvironmentVariable($bootstrapUserIdVariable, $originalBootstrapUserId, "Process")
    }
}

function ConvertFrom-GraceJson {
    param([Parameter(Mandatory)][string] $Text)

    $jsonStart = $Text.IndexOf('{')
    if ($jsonStart -lt 0) {
        throw "Grace CLI did not return JSON."
    }

    return $Text.Substring($jsonStart) | ConvertFrom-Json
}

function Invoke-GraceJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $originalServerUri = $env:GRACE_SERVER_URI

    try {
        $env:GRACE_SERVER_URI = $GraceServerUri
        $outputLines = @(& $cliPath @Arguments 2>&1 | ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
        $output = $outputLines -join [Environment]::NewLine

        if ($exitCode -ne 0) {
            throw "Grace CLI exited with code $exitCode. $output"
        }

        return ConvertFrom-GraceJson -Text $output
    }
    finally {
        $env:GRACE_SERVER_URI = $originalServerUri
    }
}

function Wait-GraceIdentity {
    param([Parameter(Mandatory)][System.Diagnostics.Process] $Process)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $lastError = $null
    $lastStatusAt = [DateTimeOffset]::MinValue

    Write-Status "Waiting for Grace authentication at $GraceServerUri."

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Aspire AppHost exited with code $($Process.ExitCode). See $stderrPath."
        }

        try {
            $oidcConfigUri = "$($GraceServerUri.TrimEnd('/'))/authenticate/oidc/config"
            $response = Invoke-WebRequest -Uri $oidcConfigUri -TimeoutSec 3 -SkipHttpErrorCheck
            if ($response.StatusCode -ne 200) {
                throw "Grace readiness endpoint returned HTTP $($response.StatusCode)."
            }

            $whoAmI = Invoke-GraceJson -Arguments @("authenticate", "whoami", "--output", "Json")
            $userId = [string] $whoAmI.ReturnValue.GraceUserId

            if (-not [string]::IsNullOrWhiteSpace($userId)) {
                Write-Status "Grace authenticated the current CLI identity."
                return $userId
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        if (([DateTimeOffset]::UtcNow - $lastStatusAt).TotalSeconds -ge 15) {
            Write-Status "Grace Server is not ready yet; continuing to wait."
            $lastStatusAt = [DateTimeOffset]::UtcNow
        }

        Start-Sleep -Seconds 2
    }

    throw "Grace did not authenticate the current CLI identity within $StartupTimeoutSeconds seconds. Last result: $lastError"
}

function Test-SystemAdmin {
    $result = Invoke-GraceJson -Arguments @(
        "authorize", "check",
        "--resource", "system",
        "--operation", "SystemAdmin",
        "--output", "Json"
    )

    return $result.ReturnValue.PSObject.Properties.Name -contains "allowed"
}

Assert-GraceServerUriAvailable
if ($PreflightOnly) {
    Write-Status "Grace Server URI '$GraceServerUri' is available for a new DebugAzure child."
    return
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

Write-Status "Building Grace CLI ($Configuration)."
& dotnet build $cliProject -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Grace CLI build failed with exit code $LASTEXITCODE."
}

$appHostProcess = $null

try {
    $appHostProcess = Start-AppHostProcess
    $currentUserId = Wait-GraceIdentity -Process $appHostProcess

    Write-Status "Checking whether the current identity already has SystemAdmin."
    if (Test-SystemAdmin) {
        Write-Status "Authorization is already configured. DebugAzure is ready."
    }
    else {
        Write-Status "SystemAdmin is not currently granted. Testing the empty-environment bootstrap path once."
        Stop-AppHostProcess -Process $appHostProcess
        $appHostProcess = Start-AppHostProcess -BootstrapUserId $currentUserId
        $restartedUserId = Wait-GraceIdentity -Process $appHostProcess

        if ($restartedUserId -ne $currentUserId) {
            throw "The authenticated Grace user changed during bootstrap restart."
        }

        Write-Status "Rechecking SystemAdmin after the bootstrap restart."
        if (-not (Test-SystemAdmin)) {
            Stop-AppHostProcess -Process $appHostProcess
            throw "This environment already has authorization state. An existing SystemAdmin must grant access to the current identity."
        }

        Write-Status "The new environment granted SystemAdmin to the current authenticated identity."
    }

    Write-Status "DebugAzure remains running as PID $($appHostProcess.Id)."
    Write-Status "Standard output: $stdoutPath"
    Write-Status "Standard error:  $stderrPath"
}
catch {
    if ($null -ne $appHostProcess) {
        Stop-AppHostProcess -Process $appHostProcess
    }

    Write-Error $_
    exit 1
}
