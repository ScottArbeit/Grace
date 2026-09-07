[CmdletBinding()]
param([string] $HostedResponsePath)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$scope = @{ OwnerId=[guid]::NewGuid().ToString(); OrganizationId=[guid]::NewGuid().ToString(); RepositoryId=[guid]::NewGuid().ToString() }
$directory = Join-Path ([IO.Path]::GetTempPath()) "grace-1062-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$destination = Join-Path $directory 'result.json'
. (Join-Path $PSScriptRoot '../diagnose-artifact-size.ps1') @scope -OutputPath $destination
$parameters = $scope.Clone()
$parameters.OutputPath = $destination
$oldUri = $env:GRACE_SERVER_URI
$oldToken = $env:GRACE_TOKEN
$env:GRACE_SERVER_URI = 'https://diagnostic.invalid'
$env:GRACE_TOKEN = 'test-only'
$report = @{ ReturnValue=@{ Scope=$scope; DeclaredArtifactBytes=0L; DistinctArtifactCount=1L;
    EnumerationStartedAt='2026-09-07T00:00:00.123456789Z'; EnumerationFinishedAt='2026-09-07T00:00:00.123456790Z' };
    CorrelationId='test'; EventTime='2026-09-07T00:00:00.123456790Z'; Properties=@{} }
$script:response = @{ StatusCode=200; Content=($report | ConvertTo-Json -Depth 8) }
$script:transportFailure = $false
$script:calls = 0

# Replaces only HTTP transport; validates the actual route, credentials and explicit scope sent by the command.
function Invoke-WebRequest {
    param([uri]$Uri,[string]$Method,[hashtable]$Headers,[string]$ContentType,[string]$Body,[switch]$SkipHttpErrorCheck,[int]$TimeoutSec)
    $script:calls++
    if ($script:transportFailure) { throw 'Simulated transport failure.' }
    if ($Uri.AbsolutePath -cne '/admin/artifact-size/diagnose' -or $Method -cne 'Post' -or
        $Headers.Authorization -cne 'Bearer test-only' -or $ContentType -cne 'application/json' -or
        -not $SkipHttpErrorCheck -or $TimeoutSec -ne 45) { throw 'Unexpected diagnostic request.' }
    $sent = $Body | ConvertFrom-Json -AsHashtable
    if ($sent.Count -ne 3) { throw 'Diagnostic must send only the three scope identifiers.' }
    foreach ($name in @('OwnerId','OrganizationId','RepositoryId')) {
        if ([guid]$sent[$name] -ne [guid]$parameters[$name]) { throw 'Unexpected request scope.' }
    }
    return $script:response
}

# Requires every failed attempt to preserve the previous result and remove only its own staging file.
function Assert-PreservedFailure {
    param([string]$Case)
    [IO.File]::WriteAllText($destination,'previous-success')
    $failed = $false
    try { Invoke-ArtifactSizeDiagnosis $parameters } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText($destination) -cne 'previous-success') { throw "Failed preservation: $Case" }
    if (@(Get-ChildItem -LiteralPath $directory -Force -Filter '*.tmp').Count -ne 0) { throw "Staging residue: $Case" }
    Write-Output "PASS: $Case preserves previous output"
}

try {
    Invoke-ArtifactSizeDiagnosis $parameters
    if ([IO.File]::ReadAllText($destination) -cne $script:response.Content) { throw 'Zero or nanoseconds changed.' }
    $report.ReturnValue.DeclaredArtifactBytes = 13L
    $report.ReturnValue.DistinctArtifactCount = 2L
    $script:response.Content = $report | ConvertTo-Json -Depth 8
    Invoke-ArtifactSizeDiagnosis $parameters
    if ([IO.File]::ReadAllText($destination) -cne $script:response.Content -or $script:calls -ne 2) { throw 'Each invocation must save a fresh complete response.' }
    Write-Output 'PASS: zero and positive quantities preserve exact JSON and nine-digit timestamps'
    $script:transportFailure = $true
    Assert-PreservedFailure 'transport failure'
    $script:transportFailure = $false
    $script:response.StatusCode = 503
    Assert-PreservedFailure 'HTTP failure'
    $script:response.StatusCode = 200
    foreach ($case in @('scope','negative','count-negative','fraction','overflow','missing','nanoseconds','window','invalid-time','invalid-json','DirectoryVersion','TextContent')) {
        $changed = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        switch ($case) {
            scope { $changed.ReturnValue.Scope.RepositoryId = [guid]::NewGuid().ToString() }
            negative { $changed.ReturnValue.DeclaredArtifactBytes = -1 }
            count-negative { $changed.ReturnValue.DistinctArtifactCount = -1 }
            fraction { $changed.ReturnValue.DeclaredArtifactBytes = 1.5 }
            overflow { $changed.ReturnValue.DeclaredArtifactBytes = [decimal]::Parse('9223372036854775808') }
            missing { $changed.ReturnValue.Remove('DistinctArtifactCount') }
            nanoseconds { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-07T00:00:00.123456788Z' }
            window { $changed.ReturnValue.EnumerationFinishedAt = '2026-09-06T00:00:00Z' }
            invalid-time { $changed.ReturnValue.EnumerationFinishedAt = '2026-02-30T00:00:00Z' }
            DirectoryVersion {
                $changed.ReturnValue.Remove('DeclaredArtifactBytes'); $changed.ReturnValue.Remove('DistinctArtifactCount')
                $changed.ReturnValue.DeclaredLogicalBytes = 13L; $changed.ReturnValue.DistinctContentCount = 2L
            }
            TextContent {
                $changed.ReturnValue.Remove('DeclaredArtifactBytes'); $changed.ReturnValue.Remove('DistinctArtifactCount')
                $changed.ReturnValue.DeclaredTextContentUtf8Bytes = 13L; $changed.ReturnValue.DistinctTextContentCount = 2L
            }
        }
        $script:response.Content = if ($case -eq 'invalid-json') { '{' } else { $changed | ConvertTo-Json -Depth 8 }
        Assert-PreservedFailure $case
    }
    $script:response.Content = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($destination,'previous-success')
    $locked = [IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $failed = $false
        try { Invoke-ArtifactSizeDiagnosis $parameters } catch { $failed = $true }
        if (-not $failed) { throw 'Locked destination should reject atomic publication on supported Windows.' }
    } finally { $locked.Dispose() }
    if ([IO.File]::ReadAllText($destination) -cne 'previous-success' -or @(Get-ChildItem $directory -Force -Filter '*.tmp').Count -ne 0) { throw 'Local publication failure changed output or left staging residue.' }
    Write-Output 'PASS: local atomic publication failure preserves previous output'
    $parameters.OwnerId = [guid]::Empty.ToString()
    Assert-PreservedFailure 'invalid request ID'
    if ($HostedResponsePath) {
        $hostedPath = (Resolve-Path -LiteralPath $HostedResponsePath).Path
        $script:response.Content = [IO.File]::ReadAllText($hostedPath)
        $hosted = $script:response.Content | ConvertFrom-Json -AsHashtable
        foreach ($name in @('OwnerId','OrganizationId','RepositoryId')) { $parameters[$name] = $hosted.ReturnValue.Scope[$name] }
        Invoke-ArtifactSizeDiagnosis $parameters
        if ((Get-FileHash -LiteralPath $destination).Hash -cne (Get-FileHash -LiteralPath $hostedPath).Hash) { throw 'Hosted envelope bytes were changed.' }
        Write-Output "PASS: actual hosted envelope published byte-identically; SHA256=$((Get-FileHash -LiteralPath $destination).Hash)"
    }
    Write-Output 'PASS: Artifact operator validation and output preservation'
} finally {
    $env:GRACE_SERVER_URI = $oldUri
    $env:GRACE_TOKEN = $oldToken
    Get-ChildItem -LiteralPath $directory -File | Remove-Item -Force
    Remove-Item -LiteralPath $directory
}
