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

. $scriptPath `
    -ReferenceId '11111111-1111-1111-1111-111111111111' `
    -MaxRelationships 1 `
    -OutputPath $outputPath

Assert-True -Condition ((Get-DiagnosisExitCode -Outcome 'verifiedComplete') -eq 0) -Message 'VerifiedComplete must map to exit code 0.'
Assert-True -Condition ((Get-DiagnosisExitCode -Outcome 'incompleteRetain') -eq 2) -Message 'IncompleteRetain must map to exit code 2.'
Assert-True -Condition ((Get-DiagnosisExitCode -Outcome 'failedRetain') -eq 3) -Message 'FailedRetain must map to exit code 3.'

$unsignedJson = '{"SchemaVersion":"grace.manifest-contribution-diagnosis.v1","Outcome":"incompleteRetain"}'
$unsignedBytes = [Text.Encoding]::UTF8.GetBytes($unsignedJson)
$hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($unsignedBytes))
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

Write-Output 'diagnose-manifest-contribution.ps1 focused tests passed.'
