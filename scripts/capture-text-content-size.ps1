[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ObservationId,
    [ValidateSet("Capture", "Read")][string] $Mode = "Capture",
    [Parameter(Mandatory)][string] $OwnerId,
    [Parameter(Mandatory)][string] $OrganizationId,
    [Parameter(Mandatory)][string] $RepositoryId,
    [Parameter(Mandatory)][string] $OutputPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Validates the complete success envelope and its requested scope before creating any output file.
function Test-TextContentObservationResponse {
    param([string] $Json, [hashtable] $Scope, [guid] $ObservationId)

    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        $root = $document.RootElement
        $value = $root.GetProperty('ReturnValue')
        if ($value.ValueKind -ne 'Object' -or $root.GetProperty('Properties').ValueKind -ne 'Object' -or
            [string]::IsNullOrWhiteSpace($root.GetProperty('CorrelationId').GetString())) {
            throw 'The server did not return a complete Grace success envelope.'
        }
        if ([guid]::Parse($value.GetProperty('ObservationId').GetString()) -ne $ObservationId) {
            throw 'The returned ObservationId does not match the request.'
        }
        $returnedScope = $value.GetProperty('Scope')
        foreach ($name in @('OwnerId', 'OrganizationId', 'RepositoryId')) {
            if ([guid]::Parse($returnedScope.GetProperty($name).GetString()) -ne [guid]::Parse($Scope[$name])) {
                throw 'The observation scope does not match the requested repository.'
            }
        }
        foreach ($name in @('DeclaredTextContentUtf8Bytes', 'DistinctTextContentCount')) {
            $quantity = 0L
            if (-not $value.GetProperty($name).TryGetInt64([ref] $quantity) -or $quantity -lt 0) {
                throw "$name must be a nonnegative 64-bit integer."
            }
        }
        $times = @{}
        foreach ($name in @('EnumerationStartedAt', 'EnumerationFinishedAt')) {
            $text = $value.GetProperty($name).GetString()
            $parsed = [DateTimeOffset]::MinValue
            if ($text -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?Z$' -or
                -not [DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::None, [ref] $parsed)) {
                throw 'The observation must contain valid UTC enumeration times.'
            }
            # Compare a normalized UTC string to preserve all nine fractional digits.
            $parts = $text.TrimEnd('Z').Split('.')
            $fraction = if ($parts.Length -eq 2) { $parts[1].PadRight(9, '0') } else { '000000000' }
            $times[$name] = $parts[0] + '.' + $fraction
        }
        if ([string]::CompareOrdinal($times.EnumerationFinishedAt, $times.EnumerationStartedAt) -lt 0) { throw 'The enumeration read window is reversed.' }
        $eventTime = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($root.GetProperty('EventTime').GetString(), [ref] $eventTime)) {
            throw 'The success envelope is missing a valid EventTime.'
        }
    }
    finally { $document.Dispose() }
}

# Reads one caller-cancellable admin observation and publishes validated JSON in the destination directory.
function Invoke-TextContentObservation {
    param([hashtable] $BoundParameters)

    $scope = @{}
    foreach ($name in @('OwnerId', 'OrganizationId', 'RepositoryId')) {
        $id = [guid]::Empty
        if (-not [guid]::TryParse($BoundParameters[$name], [ref] $id) -or $id -eq [guid]::Empty) {
            throw "$name must be a non-empty GUID."
        }
        $scope[$name] = $id.ToString()
    }
    $observationId = [guid]::Empty
    if (-not [guid]::TryParse($BoundParameters.ObservationId, [ref] $observationId) -or $observationId -eq [guid]::Empty) {
        throw 'ObservationId must be a non-empty GUID.'
    }
    $mode = if ($BoundParameters.ContainsKey('Mode')) { $BoundParameters.Mode } else { 'Capture' }
    if ($mode -notin @('Capture', 'Read')) { throw 'Mode must be Capture or Read.' }
    $serverUri = $null
    if (-not [uri]::TryCreate($env:GRACE_SERVER_URI, [UriKind]::Absolute, [ref] $serverUri) -or
        $serverUri.Scheme -notin @('http', 'https')) { throw 'GRACE_SERVER_URI must contain the Grace Server HTTP(S) base URI.' }
    if ([string]::IsNullOrWhiteSpace($env:GRACE_TOKEN)) { throw 'GRACE_TOKEN must contain a SystemAdmin token.' }
    $destination = [IO.Path]::GetFullPath($BoundParameters.OutputPath)
    $parent = [IO.Path]::GetDirectoryName($destination)
    if (-not [IO.Directory]::Exists($parent) -or [IO.Directory]::Exists($destination)) {
        throw 'OutputPath must name a file in an existing directory.'
    }
    $route = "/admin/text-content-size/observations/$observationId"
    $request = @{ Uri = [uri]::new($serverUri, $route); Method = 'Post';
        Headers = @{ Authorization = "Bearer $($env:GRACE_TOKEN)" }; ContentType = 'application/json';
        Body = ($scope | ConvertTo-Json -Compress); SkipHttpErrorCheck = $true;
        ConnectionTimeoutSeconds = 0; OperationTimeoutSeconds = 0 }
    if ($mode -eq 'Read') {
        $query = @('OwnerId', 'OrganizationId', 'RepositoryId') | ForEach-Object { "$_=$($scope[$_])" }
        $request.Uri = [uri]::new($serverUri, $route + '?' + ($query -join '&'))
        $request.Method = 'Get'
        $request.Remove('Body')
    }
    $retryAdvice = if ($mode -eq 'Capture') {
        "The server may have committed the observation. Retry with the same ObservationId '$observationId'."
    } else {
        "Retry reading the same ObservationId '$observationId'."
    }
    try { $response = Invoke-WebRequest @request }
    catch { throw "Grace Server request failed. Local output was not changed. $retryAdvice" }
    if ($response.StatusCode -ne 200) {
        throw "Grace Server returned HTTP $($response.StatusCode). Local output was not changed. $retryAdvice"
    }
    try { Test-TextContentObservationResponse -Json $response.Content -Scope $scope -ObservationId $observationId }
    catch { throw "Grace Server response validation failed: $($_.Exception.Message) Local output was not changed. $retryAdvice" }
    $temporary = Join-Path $parent ".$([IO.Path]::GetFileName($destination)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $response.Content, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $destination, $true)
    }
    finally { if ([IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary -Force } }
    Write-Host "TextContent declaration observation saved to '$destination'."
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-TextContentObservation -BoundParameters $PSBoundParameters
        exit 0
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 4
    }
}
