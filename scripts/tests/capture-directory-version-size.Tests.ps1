[CmdletBinding()]
param([string] $HostedResponsePath)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$scope = @{ OwnerId = [guid]::NewGuid().ToString(); OrganizationId = [guid]::NewGuid().ToString(); RepositoryId = [guid]::NewGuid().ToString(); ObservationId = [guid]::NewGuid().ToString() }
$directory = Join-Path ([IO.Path]::GetTempPath()) "grace-1056-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$destination = Join-Path $directory 'result.json'
. (Join-Path $PSScriptRoot '../capture-directory-version-size.ps1') @scope -OutputPath $destination
$parameters = $scope.Clone()
$parameters.OutputPath = $destination
$oldUri = $env:GRACE_SERVER_URI
$oldToken = $env:GRACE_TOKEN
$env:GRACE_SERVER_URI = 'https://diagnostic.invalid'
$env:GRACE_TOKEN = 'test-only'
$report = @{ ReturnValue = @{ ObservationId = $scope.ObservationId; Scope = $scope; DeclaredLogicalBytes = 0L; DistinctContentCount = 1L;
    EnumerationStartedAt = '2026-09-07T00:00:00.123456789Z'; EnumerationFinishedAt = '2026-09-07T00:00:00.123456790Z' };
    CorrelationId = 'test'; EventTime = '2026-09-07T00:00:00.123456790Z'; Properties = @{} }
$script:response = @{ StatusCode = 200; Content = ($report | ConvertTo-Json -Depth 8) }

# Replaces only the command's HTTP call so publication exercises real local file operations.
function Invoke-WebRequest {
    param($Uri, $Method, $Headers, $ContentType, $Body, $SkipHttpErrorCheck, $TimeoutSec)
    if ($script:transportFailure) { throw 'Simulated transport failure.' }
    $script:lastMethod = $Method
    $script:lastUri = $Uri
    return $script:response
}
$script:transportFailure = $false

# Requires malformed or failed responses to preserve the last saved diagnostic without staging residue.
function Assert-PreservedFailure {
    param([string] $Case)
    [IO.File]::WriteAllText($destination, 'previous-success')
    $failed = $false
    try { Invoke-DirectoryVersionObservation $parameters } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText($destination) -cne 'previous-success') { throw "Failed preservation: $Case" }
    if (@(Get-ChildItem $directory -Force -Filter '*.tmp').Count -ne 0) { throw "Unexpected staging output: $Case" }
    Write-Output "PASS: $Case preserves previous output"
}

try {
    Invoke-DirectoryVersionObservation $parameters
    if ($script:lastMethod -ne 'Post' -or $script:lastUri.AbsolutePath -notmatch '/observations/') { throw 'Capture request is malformed.' }
    $saved = Get-Content $destination -Raw | ConvertFrom-Json
    if ($saved.ReturnValue.DeclaredLogicalBytes -ne 0 -or $saved.ReturnValue.DistinctContentCount -ne 1) { throw 'Known zero was not saved.' }
    if ([IO.File]::ReadAllText($destination) -cne $script:response.Content) { throw 'Original JSON was changed.' }
    $parameters.Mode = 'Read'
    Invoke-DirectoryVersionObservation $parameters
    if ([IO.File]::ReadAllText($destination) -cne $script:response.Content) { throw 'Read changed original JSON.' }
    if ($script:lastMethod -ne 'Get' -or $script:lastUri.Query -notmatch 'OwnerId=') { throw 'Read request is malformed.' }
    $parameters.Mode = 'Capture'
    $script:transportFailure = $true
    Assert-PreservedFailure 'transport failure'
    $script:transportFailure = $false
    Write-Output 'PASS: capture and read preserve original zero and nine-digit JSON'
    $script:response.StatusCode = 503
    Assert-PreservedFailure 'HTTP failure'
    $script:response.StatusCode = 200
    foreach ($case in @('identity', 'nanoseconds', 'scope', 'negative', 'fraction', 'overflow', 'missing', 'window', 'invalid-json')) {
        $changed = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        switch ($case) {
            identity { $changed.ReturnValue.ObservationId = [guid]::NewGuid().ToString() }
            nanoseconds { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-07T00:00:00.123456788Z' }
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
    if ($HostedResponsePath) {
        $script:response.Content = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $HostedResponsePath))
        $hosted = $script:response.Content | ConvertFrom-Json -AsHashtable
        foreach ($name in @('OwnerId', 'OrganizationId', 'RepositoryId')) { $parameters[$name] = $hosted.ReturnValue.Scope[$name] }
        $parameters.ObservationId = $hosted.ReturnValue.ObservationId
        $parameters.Mode = 'Capture'
        Invoke-DirectoryVersionObservation $parameters
        if ([IO.File]::ReadAllText($destination) -cne $script:response.Content) { throw 'Hosted envelope was changed.' }
        Write-Output "PASS: actual hosted envelope validated and published unchanged; SHA256=$((Get-FileHash $destination).Hash)"
    }
    Write-Output 'PASS: operator validation and output preservation assertions'
}
finally {
    $env:GRACE_SERVER_URI = $oldUri
    $env:GRACE_TOKEN = $oldToken
    Get-ChildItem -LiteralPath $directory -File | Remove-Item -Force
    Remove-Item -LiteralPath $directory
}
