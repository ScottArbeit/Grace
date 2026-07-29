[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool] $Condition,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$scriptPath = Join-Path $PSScriptRoot '..\repair-manifest-contribution.ps1'
$source = [IO.File]::ReadAllText($scriptPath)
[scriptblock]::Create($source) | Out-Null

foreach ($forbidden in @('CosmosConnection', 'ServiceBusConnection', 'RedisConnection', 'StorageConnection')) {
    Assert-True -Condition ($source -notmatch [regex]::Escape($forbidden)) `
        -Message "The repair script must not accept direct storage credentials: $forbidden"
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "grace-repair-script-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
$reportPath = Join-Path $temporaryDirectory 'diagnosis.json'

function Invoke-RepairChildProcess {
    param(
        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Process -Id $PID).Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['GRACE_SERVER_URI'] = 'https://repair.invalid'
    $startInfo.Environment['GRACE_TOKEN'] = 'focused-system-admin-token'
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-NonInteractive')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($scriptPath)

    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $process.Start() | Out-Null
    $output = $process.StandardOutput.ReadToEnd()
    $errorOutput = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    return [pscustomobject] @{ ExitCode = $process.ExitCode; Output = $output; Error = $errorOutput }
}

try {
    $unsignedJson = '{"SchemaVersion":"grace.manifest-contribution-diagnosis.v1","Outcome":"IncompleteRetain"}'
    $bytes = [Text.Encoding]::UTF8.GetBytes($unsignedJson)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $signed = [Text.Json.Nodes.JsonNode]::Parse($unsignedJson)
    $signed['ReportSha256'] = $hash
    $signedJson = $signed.ToJsonString()
    [IO.File]::WriteAllText($reportPath, $signedJson, [Text.UTF8Encoding]::new($false))

    . $scriptPath -ReportPath $reportPath -ExpectedReportSha256 $hash

    Assert-True -Condition (Test-ManifestContributionReportSha256 -Json $signedJson) `
        -Message 'A matching diagnosis report SHA-256 must verify.'
    Assert-True -Condition (-not (Test-ManifestContributionReportSha256 -Json ($signedJson.Replace('IncompleteRetain', 'FailedRetain')))) `
        -Message 'Tampered diagnosis content must fail SHA-256 verification.'
    Assert-True -Condition ((Get-RepairExitCode -Outcome 'VerifiedComplete') -eq 0) `
        -Message 'VerifiedComplete must map to exit 0.'
    Assert-True -Condition ((Get-RepairExitCode -Outcome 'IncompleteRetain') -eq 2) `
        -Message 'IncompleteRetain must map to exit 2.'
    Assert-True -Condition ((Get-RepairExitCode -Outcome 'FailedRetain') -eq 3) `
        -Message 'FailedRetain must map to exit 3.'

    $invalidInvocation = Invoke-RepairChildProcess -ArgumentList @(
        '-ReportPath', $reportPath,
        '-ExpectedReportSha256', ('0' * 64)
    )

    Assert-True -Condition ($invalidInvocation.ExitCode -eq 4) `
        -Message "Report-validation failure must exit 4, got $($invalidInvocation.ExitCode)."
    Assert-True -Condition ($invalidInvocation.Error -match 'does not match') `
        -Message 'Exit 4 must include useful report-validation error text.'

    $env:GRACE_SERVER_URI = 'https://repair.invalid'
    $env:GRACE_TOKEN = 'focused-system-admin-token'
    $script:requestBody = $null

    function global:Invoke-WebRequest {
        param($Uri, $Method, $Headers, $ContentType, $Body, [switch] $SkipHttpErrorCheck)

        $script:requestBody = $Body | ConvertFrom-Json
        $executeValue = [bool] $script:requestBody.Execute
        $responseBody = @{
            SchemaVersion = 'grace.manifest-contribution-repair.v1'
            DiagnosisReportSha256 = $hash
            Execute = $executeValue
            ProposedActions = @(@{ Kind = 'ReconcileCounter'; Identity = 'target' })
            AppliedActions = @()
            Outcome = 'IncompleteRetain'
            Message = 'focused'
        } | ConvertTo-Json -Compress

        return [pscustomobject] @{ StatusCode = 200; Content = $responseBody }
    }

    $dryRun = @{
        ReportPath = $reportPath
        ExpectedReportSha256 = $hash
        Execute = $false
    }

    $dryRunOutput = Invoke-ManifestContributionRepair -BoundParameters $dryRun
    Assert-True -Condition (-not [bool] $script:requestBody.Execute) -Message 'Dry run must be the default request.'
    Assert-True -Condition ($dryRunOutput -eq 2) -Message 'Repair-needed dry run must return exit 2.'

    $executeRequest = $dryRun.Clone()
    $executeRequest.Execute = $true
    $executeOutput = Invoke-ManifestContributionRepair -BoundParameters $executeRequest
    Assert-True -Condition ([bool] $script:requestBody.Execute) -Message 'Only explicit Execute may request mutation.'
    Assert-True -Condition ($executeOutput -eq 2) -Message 'Incomplete execute must return exit 2.'
}
finally {
    Remove-Item -LiteralPath function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
}

Write-Output 'repair-manifest-contribution.ps1 focused tests passed.'
