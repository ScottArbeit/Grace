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

$scriptPath = Join-Path $PSScriptRoot '..\diagnose-manifest-contribution.ps1'
$outputPath = Join-Path ([IO.Path]::GetTempPath()) 'manifest-contribution-diagnosis-test.json'

function Invoke-DiagnosisChildProcess {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Process -Id $PID).Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['GRACE_SERVER_URI'] = 'https://diagnosis.invalid'
    $startInfo.Environment['GRACE_TOKEN'] = 'focused-system-admin-token'
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-NonInteractive')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($FilePath)

    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $process.Start() | Out-Null
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    return [pscustomobject] @{
        ExitCode = $process.ExitCode
        Output = $standardOutput
        Error = $standardError
    }
}

. $scriptPath `
    -ReferenceId '11111111-1111-1111-1111-111111111111' `
    -MaxRelationships 1 `
    -OutputPath $outputPath

Assert-True -Condition ((Get-DiagnosisExitCode -Outcome 'verifiedComplete') -eq 0) -Message 'VerifiedComplete must map to exit code 0.'
Assert-True -Condition ((Get-DiagnosisExitCode -Outcome 'incompleteRetain') -eq 2) -Message 'IncompleteRetain must map to exit code 2.'
Assert-True -Condition ((Get-DiagnosisExitCode -Outcome 'failedRetain') -eq 3) -Message 'FailedRetain must map to exit code 3.'

$unsignedJson = '{"SchemaVersion":"grace.manifest-contribution-diagnosis.v1","Outcome":"incompleteRetain"}'
$unsignedBytes = [Text.Encoding]::UTF8.GetBytes($unsignedJson)
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($unsignedBytes)).ToLowerInvariant()
$signed = [Text.Json.Nodes.JsonNode]::Parse($unsignedJson)
$signed['ReportSha256'] = $hash
$signedJson = $signed.ToJsonString()

Assert-True -Condition (Test-ReportSha256 -Json $signedJson) -Message 'A matching report SHA-256 must verify.'
Assert-True -Condition (-not (Test-ReportSha256 -Json ($signedJson.Replace('incompleteRetain', 'failedRetain')))) `
    -Message 'Changing signed report content must fail SHA-256 verification.'

$validatedPath = Get-ValidatedOutputPath -Path $outputPath
Assert-True -Condition ([IO.Path]::IsPathFullyQualified($validatedPath)) -Message 'OutputPath validation must produce an absolute path.'

$invalidGuidRejected = $false

try {
    Test-NonEmptyGuid -Name 'ReferenceId' -Value ([guid]::Empty.ToString())
}
catch {
    $invalidGuidRejected = $true
}

Assert-True -Condition $invalidGuidRejected -Message 'An empty GUID must be rejected before any HTTP request.'

$childTestDirectory = Join-Path ([IO.Path]::GetTempPath()) "grace-diagnosis-process-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($childTestDirectory) | Out-Null

try {
    $childOutputPath = Join-Path $childTestDirectory 'report.json'
    $localValidation = Invoke-DiagnosisChildProcess `
        -FilePath $scriptPath `
        -ArgumentList @(
            '-ReferenceId', 'not-a-guid',
            '-MaxRelationships', '1',
            '-OutputPath', $childOutputPath
        )

    Assert-True -Condition ($localValidation.ExitCode -eq 4) `
        -Message "Local validation failures must exit 4, got $($localValidation.ExitCode). Error: $($localValidation.Error)"
    Assert-True -Condition ($localValidation.Error -match 'ReferenceId must be a non-empty GUID') `
        -Message 'Local validation failures must print useful error text.'

    $wrapperPath = Join-Path $childTestDirectory 'invoke-diagnosis-failure.ps1'
    $wrapper = @'
param(
    [Parameter(Mandatory)]
    [string] $TargetScript,

    [Parameter(Mandatory)]
    [ValidateSet('Transport', 'Digest', 'FileWrite')]
    [string] $Mode,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function New-SignedReportJson {
    $unsignedJson = '{"SchemaVersion":"grace.manifest-contribution-diagnosis.v1","Outcome":"IncompleteRetain"}'
    $bytes = [Text.Encoding]::UTF8.GetBytes($unsignedJson)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $signed = [Text.Json.Nodes.JsonNode]::Parse($unsignedJson)
    $signed['ReportSha256'] = $hash
    return $signed.ToJsonString()
}

switch ($Mode) {
    'Transport' {
        function global:Invoke-WebRequest {
            throw 'Focused transport failure.'
        }
    }
    'Digest' {
        function global:Invoke-WebRequest {
            return [pscustomobject] @{
                StatusCode = 200
                Content = '{"Outcome":"IncompleteRetain","ReportSha256":"invalid"}'
            }
        }
    }
    'FileWrite' {
        $script:responseJson = New-SignedReportJson

        function global:Invoke-WebRequest {
            return [pscustomobject] @{
                StatusCode = 200
                Content = $script:responseJson
            }
        }

        function global:Move-Item {
            throw 'Focused file replacement failure.'
        }
    }
}

& $TargetScript `
    -ReferenceId '11111111-1111-1111-1111-111111111111' `
    -MaxRelationships 1 `
    -OutputPath $OutputPath

exit $LASTEXITCODE
'@

    [IO.File]::WriteAllText($wrapperPath, $wrapper, [Text.UTF8Encoding]::new($false))

    foreach ($mode in @('Transport', 'Digest', 'FileWrite')) {
        $failure = Invoke-DiagnosisChildProcess `
            -FilePath $wrapperPath `
            -ArgumentList @(
                '-TargetScript', $scriptPath,
                '-Mode', $mode,
                '-OutputPath', $childOutputPath
            )

        Assert-True -Condition ($failure.ExitCode -eq 4) `
            -Message "$mode failures must exit 4, got $($failure.ExitCode). Error: $($failure.Error)"
        Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($failure.Error)) `
            -Message "$mode failures must print useful error text."
    }
}
finally {
    Remove-Item -LiteralPath $childTestDirectory -Recurse -Force
}

Write-Output 'diagnose-manifest-contribution.ps1 focused tests passed.'
