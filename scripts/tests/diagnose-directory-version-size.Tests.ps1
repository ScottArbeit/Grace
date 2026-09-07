[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$scope = @{ OwnerId = [guid]::NewGuid().ToString(); OrganizationId = [guid]::NewGuid().ToString(); RepositoryId = [guid]::NewGuid().ToString() }
$directory = Join-Path ([IO.Path]::GetTempPath()) "grace-1047-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$destination = Join-Path $directory 'result.json'
. (Join-Path $PSScriptRoot '../diagnose-directory-version-size.ps1') @scope -OutputPath $destination
$parameters = $scope.Clone()
$parameters.OutputPath = $destination
$oldUri = $env:GRACE_SERVER_URI
$oldToken = $env:GRACE_TOKEN
$env:GRACE_SERVER_URI = 'https://diagnostic.invalid'
$env:GRACE_TOKEN = 'test-only'
$report = @{ ReturnValue = @{ Scope = $scope; DeclaredLogicalBytes = 0L; DistinctContentCount = 1L;
    EnumerationStartedAt = '2026-09-07T00:00:00Z'; EnumerationFinishedAt = '2026-09-07T00:00:01Z' };
    CorrelationId = 'test'; EventTime = '2026-09-07T00:00:01Z'; Properties = @{} }
$script:response = @{ StatusCode = 200; Content = ($report | ConvertTo-Json -Depth 8) }

# Replaces only the command's HTTP call so publication exercises real local file operations.
function Invoke-WebRequest { return $script:response }

# Requires malformed or failed responses to preserve the last saved diagnostic without staging residue.
function Assert-PreservedFailure {
    param([string] $Case)
    [IO.File]::WriteAllText($destination, 'previous-success')
    $failed = $false
    try { Invoke-DirectoryVersionSizeDiagnosis $parameters } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText($destination) -cne 'previous-success') { throw "Failed preservation: $Case" }
    if (@(Get-ChildItem $directory -Force -Filter '*.tmp').Count -ne 0) { throw "Unexpected staging output: $Case" }
    Write-Output "PASS: $Case preserves previous output"
}

try {
    Invoke-DirectoryVersionSizeDiagnosis $parameters
    $saved = Get-Content $destination -Raw | ConvertFrom-Json
    if ($saved.ReturnValue.DeclaredLogicalBytes -ne 0 -or $saved.ReturnValue.DistinctContentCount -ne 1) { throw 'Known zero was not saved.' }
    Write-Output 'PASS: complete scoped zero saves and reopens'
    $script:response.StatusCode = 503
    Assert-PreservedFailure 'HTTP failure'
    $script:response.StatusCode = 200
    foreach ($case in @('scope', 'negative', 'fraction', 'overflow', 'missing', 'window', 'invalid-json')) {
        $changed = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        switch ($case) {
            scope { $changed.ReturnValue.Scope.RepositoryId = [guid]::NewGuid().ToString() }
            negative { $changed.ReturnValue.DeclaredLogicalBytes = -1 }
            fraction { $changed.ReturnValue.DeclaredLogicalBytes = 1.5 }
            overflow { $changed.ReturnValue.DeclaredLogicalBytes = [decimal]::Parse('9223372036854775808') }
            missing { $changed.ReturnValue.Remove('DistinctContentCount') }
            window { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-06T00:00:00Z' }
        }
        $script:response.Content = if ($case -eq 'invalid-json') { '{' } else { $changed | ConvertTo-Json -Depth 8 }
        Assert-PreservedFailure $case
    }
    $parameters.OwnerId = [guid]::Empty.ToString()
    Assert-PreservedFailure 'invalid request ID'
    Write-Output 'PASS: 10 script publication assertions'
}
finally {
    $env:GRACE_SERVER_URI = $oldUri
    $env:GRACE_TOKEN = $oldToken
    Get-ChildItem -LiteralPath $directory -File | Remove-Item -Force
    Remove-Item -LiteralPath $directory
}
