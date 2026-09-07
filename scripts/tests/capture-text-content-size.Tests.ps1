[CmdletBinding()]
param([string] $HostedResponsePath)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$scope = @{ OwnerId = [guid]::NewGuid().ToString(); OrganizationId = [guid]::NewGuid().ToString(); RepositoryId = [guid]::NewGuid().ToString(); ObservationId = [guid]::NewGuid().ToString() }
$directory = Join-Path ([IO.Path]::GetTempPath()) "grace-1058-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$destination = Join-Path $directory 'result.json'
. (Join-Path $PSScriptRoot '../capture-text-content-size.ps1') @scope -OutputPath $destination
$parameters = $scope.Clone()
$parameters.OutputPath = $destination
$oldUri = $env:GRACE_SERVER_URI
$oldToken = $env:GRACE_TOKEN
$env:GRACE_SERVER_URI = 'https://diagnostic.invalid'
$env:GRACE_TOKEN = 'test-only'
$report = @{ ReturnValue = @{ ObservationId = $scope.ObservationId; Scope = $scope; DeclaredTextContentUtf8Bytes = 0L; DistinctTextContentCount = 1L;
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
    param([string] $Case, [string] $ExpectedMessage)
    [IO.File]::WriteAllText($destination, 'previous-success')
    $failed = $false
    $message = $null
    try { Invoke-TextContentObservation $parameters } catch { $failed = $true; $message = $_.Exception.Message }
    if (-not $failed -or [IO.File]::ReadAllText($destination) -cne 'previous-success') { throw "Failed preservation: $Case" }
    if ($ExpectedMessage -and $message -cne $ExpectedMessage) { throw "Unexpected failure guidance for ${Case}: $message" }
    if (@(Get-ChildItem $directory -Force -Filter '*.tmp').Count -ne 0) { throw "Unexpected staging output: $Case" }
    Write-Output "PASS: $Case preserves previous output"
}

try {
    Invoke-TextContentObservation $parameters
    if ($script:lastMethod -ne 'Post' -or $script:lastUri.AbsolutePath -notmatch '/observations/') { throw 'Capture request is malformed.' }
    $saved = Get-Content $destination -Raw | ConvertFrom-Json
    if ($saved.ReturnValue.DeclaredTextContentUtf8Bytes -ne 0 -or $saved.ReturnValue.DistinctTextContentCount -ne 1) { throw 'Known zero was not saved.' }
    if ([IO.File]::ReadAllText($destination) -cne $script:response.Content) { throw 'Original JSON was changed.' }
    $parameters.Mode = 'Read'
    Invoke-TextContentObservation $parameters
    if ([IO.File]::ReadAllText($destination) -cne $script:response.Content) { throw 'Read changed original JSON.' }
    if ($script:lastMethod -ne 'Get' -or $script:lastUri.Query -notmatch 'OwnerId=') { throw 'Read request is malformed.' }
    $parameters.Mode = 'Capture'
    $script:transportFailure = $true
    Assert-PreservedFailure 'transport failure' "Grace Server request failed. Local output was not changed. The server may have committed the observation. Retry with the same ObservationId '$($parameters.ObservationId)'."
    $parameters.Mode = 'Read'
    Assert-PreservedFailure 'read transport failure' "Grace Server request failed. Local output was not changed. Retry reading the same ObservationId '$($parameters.ObservationId)'."
    $parameters.Mode = 'Capture'
    $script:transportFailure = $false
    Write-Output 'PASS: capture and read preserve original zero and nine-digit JSON'
    $script:response.StatusCode = 503
    Assert-PreservedFailure 'HTTP 503 uncertain capture' "Grace Server returned HTTP 503. Local output was not changed. The server may have committed the observation. Retry with the same ObservationId '$($parameters.ObservationId)'."
    $parameters.Mode = 'Read'
    Assert-PreservedFailure 'HTTP 503 read' "Grace Server returned HTTP 503. Local output was not changed. Retry reading the same ObservationId '$($parameters.ObservationId)'."
    $parameters.Mode = 'Capture'
    $script:response.StatusCode = 200
    foreach ($case in @('identity', 'nanoseconds', 'scope', 'negative', 'fraction', 'overflow', 'missing', 'window', 'invalid-json', 'wrong-source')) {
        $changed = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        switch ($case) {
            identity { $changed.ReturnValue.ObservationId = [guid]::NewGuid().ToString() }
            nanoseconds { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-07T00:00:00.123456788Z' }
            scope { $changed.ReturnValue.Scope.RepositoryId = [guid]::NewGuid().ToString() }
            negative { $changed.ReturnValue.DeclaredTextContentUtf8Bytes = -1 }
            fraction { $changed.ReturnValue.DeclaredTextContentUtf8Bytes = 1.5 }
            overflow { $changed.ReturnValue.DeclaredTextContentUtf8Bytes = [decimal]::Parse('9223372036854775808') }
            missing { $changed.ReturnValue.Remove('DistinctTextContentCount') }
            wrong-source {
                $changed.ReturnValue.Remove('DeclaredTextContentUtf8Bytes')
                $changed.ReturnValue.Remove('DistinctTextContentCount')
                $changed.ReturnValue.DeclaredLogicalBytes = 0L
                $changed.ReturnValue.DistinctContentCount = 1L
            }
            window { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-06T00:00:00Z' }
        }
        $script:response.Content = if ($case -eq 'invalid-json') { '{' } else { $changed | ConvertTo-Json -Depth 8 }
        Assert-PreservedFailure $case
    }
    $script:response.Content = '{'
    [IO.File]::WriteAllText($destination, 'previous-success')
    try { Invoke-TextContentObservation $parameters; throw 'Expected malformed JSON to fail.' }
    catch {
        if ($_.Exception.Message -notlike '*Local output was not changed. The server may have committed the observation. Retry with the same ObservationId*') {
            throw "Malformed success did not retain uncertain-commit guidance: $($_.Exception.Message)"
        }
    }
    if ([IO.File]::ReadAllText($destination) -cne 'previous-success') { throw 'Malformed success changed prior output.' }
    $parameters.OwnerId = [guid]::Empty.ToString()
    Assert-PreservedFailure 'invalid request ID'
    if ($HostedResponsePath) {
        $script:response.Content = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $HostedResponsePath))
        $hosted = $script:response.Content | ConvertFrom-Json -AsHashtable
        foreach ($name in @('OwnerId', 'OrganizationId', 'RepositoryId')) { $parameters[$name] = $hosted.ReturnValue.Scope[$name] }
        $parameters.ObservationId = $hosted.ReturnValue.ObservationId
        $parameters.Mode = 'Capture'
        Invoke-TextContentObservation $parameters
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
