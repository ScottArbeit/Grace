[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$scope = @{ OwnerId = [guid]::NewGuid().ToString(); OrganizationId = [guid]::NewGuid().ToString(); RepositoryId = [guid]::NewGuid().ToString() }
$directory = Join-Path ([IO.Path]::GetTempPath()) "grace-1051-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$destination = Join-Path $directory 'result.json'
. (Join-Path $PSScriptRoot '../diagnose-text-content-size.ps1') @scope -OutputPath $destination
. (Join-Path $PSScriptRoot '../diagnose-directory-version-size.ps1') @scope -OutputPath $destination
$parameters = $scope.Clone()
$parameters.OutputPath = $destination
$oldUri = $env:GRACE_SERVER_URI
$oldToken = $env:GRACE_TOKEN
$env:GRACE_SERVER_URI = 'https://diagnostic.invalid'
$env:GRACE_TOKEN = 'test-only'
$report = @{ ReturnValue = @{ Scope = $scope; DeclaredTextContentUtf8Bytes = 0L; DistinctTextContentCount = 0L;
    EnumerationStartedAt = '2026-09-07T00:00:00Z'; EnumerationFinishedAt = '2026-09-07T00:00:01Z' };
    CorrelationId = 'test'; EventTime = '2026-09-07T00:00:01Z'; Properties = @{} }
$script:response = @{ StatusCode = 200; Content = ($report | ConvertTo-Json -Depth 8) }
$script:expectedPath = '/admin/text-content-size/diagnose'

# Verifies the operator's actual request while replacing transport so output publication still uses the filesystem.
function Invoke-WebRequest {
    param([uri] $Uri, [string] $Method, [hashtable] $Headers, [string] $ContentType, [string] $Body,
        [switch] $SkipHttpErrorCheck, [int] $TimeoutSec)
    if ($Uri.AbsolutePath -cne $script:expectedPath -or $Method -cne 'Post' -or
        $Headers.Authorization -cne 'Bearer test-only' -or $ContentType -cne 'application/json' -or
        -not $SkipHttpErrorCheck -or $TimeoutSec -ne 45) { throw 'Unexpected diagnostic request.' }
    $sent = $Body | ConvertFrom-Json -AsHashtable
    foreach ($name in $scope.Keys) {
        if ([guid] $sent[$name] -ne [guid] $scope[$name]) { throw 'Unexpected request scope.' }
    }
    return $script:response
}

# Requires a failed attempt to leave the last saved result intact and create no staging residue.
function Assert-PreservedFailure {
    param([string] $Case, [switch] $DirectoryVersion)
    [IO.File]::WriteAllText($destination, 'previous-success')
    $failed = $false
    try {
        if ($DirectoryVersion) { Invoke-DirectoryVersionSizeDiagnosis $parameters }
        else { Invoke-TextContentSizeDiagnosis $parameters }
    } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText($destination) -cne 'previous-success') { throw "Failed preservation: $Case" }
    if (@(Get-ChildItem -LiteralPath $directory -Force -Filter '*.tmp').Count -ne 0) { throw "Unexpected staging output: $Case" }
    Write-Output "PASS: $Case preserves previous output"
}

try {
    Invoke-TextContentSizeDiagnosis $parameters
    $saved = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
    if ($saved.ReturnValue.DeclaredTextContentUtf8Bytes -ne 0 -or $saved.ReturnValue.DistinctTextContentCount -ne 0) { throw 'Known zero was not saved.' }
    Write-Output 'PASS: complete scoped empty result saves and reopens'
    $report.ReturnValue.DeclaredTextContentUtf8Bytes = 6L
    $report.ReturnValue.DistinctTextContentCount = 1L
    $script:response.Content = $report | ConvertTo-Json -Depth 8
    Invoke-TextContentSizeDiagnosis $parameters
    $saved = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
    if ($saved.ReturnValue.DeclaredTextContentUtf8Bytes -ne 6 -or $saved.ReturnValue.DistinctTextContentCount -ne 1) { throw 'Nonzero result was not saved.' }
    Write-Output 'PASS: nonzero TextContent result saves and reopens'
    $script:response.StatusCode = 503
    Assert-PreservedFailure 'HTTP failure'
    $script:response.StatusCode = 200
    foreach ($case in @('scope', 'negative', 'fraction', 'overflow', 'missing', 'window', 'invalid-json', 'DirectoryVersion-response')) {
        $changed = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        switch ($case) {
            scope { $changed.ReturnValue.Scope.RepositoryId = [guid]::NewGuid().ToString() }
            negative { $changed.ReturnValue.DeclaredTextContentUtf8Bytes = -1 }
            fraction { $changed.ReturnValue.DeclaredTextContentUtf8Bytes = 1.5 }
            overflow { $changed.ReturnValue.DeclaredTextContentUtf8Bytes = [decimal]::Parse('9223372036854775808') }
            missing { $changed.ReturnValue.Remove('DistinctTextContentCount') }
            window { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-06T00:00:00Z' }
            DirectoryVersion-response {
                $changed.ReturnValue.DeclaredLogicalBytes = $changed.ReturnValue.DeclaredTextContentUtf8Bytes
                $changed.ReturnValue.DistinctContentCount = $changed.ReturnValue.DistinctTextContentCount
                $changed.ReturnValue.Remove('DeclaredTextContentUtf8Bytes')
                $changed.ReturnValue.Remove('DistinctTextContentCount')
            }
        }
        $script:response.Content = if ($case -eq 'invalid-json') { '{' } else { $changed | ConvertTo-Json -Depth 8 }
        Assert-PreservedFailure $case
    }
    $script:response.Content = $report | ConvertTo-Json -Depth 8
    $script:expectedPath = '/admin/directory-version-size/diagnose'
    Assert-PreservedFailure 'TextContent response rejected by DirectoryVersion command' -DirectoryVersion
    $script:expectedPath = '/admin/text-content-size/diagnose'
    $parameters.OwnerId = [guid]::Empty.ToString()
    Assert-PreservedFailure 'invalid request ID'
    Write-Output 'PASS: 13 operator request and publication assertions'
}
finally {
    $env:GRACE_SERVER_URI = $oldUri
    $env:GRACE_TOKEN = $oldToken
    Write-Output "Retained test output: $directory"
}
