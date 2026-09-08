[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OwnerId,
    [Parameter(Mandatory)][string] $OrganizationId,
    [Parameter(Mandatory)][string] $RepositoryId,
    [Parameter(Mandatory)][string] $OutputPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Validates the complete success envelope and its requested scope before creating any output file.
function Test-DirectoryVersionSizeResponse {
    param([string] $Json, [hashtable] $Scope)

    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        $root = $document.RootElement
        $value = $root.GetProperty('ReturnValue')
        if ($value.ValueKind -ne 'Object' -or $root.GetProperty('Properties').ValueKind -ne 'Object' -or
            [string]::IsNullOrWhiteSpace($root.GetProperty('CorrelationId').GetString())) {
            throw 'The server did not return a complete Grace success envelope.'
        }
        $returnedScope = $value.GetProperty('Scope')
        foreach ($name in @('OwnerId', 'OrganizationId', 'RepositoryId')) {
            if ([guid]::Parse($returnedScope.GetProperty($name).GetString()) -ne [guid]::Parse($Scope[$name])) {
                throw 'The diagnostic scope does not match the requested repository.'
            }
        }
        foreach ($name in @('DeclaredLogicalBytes', 'DistinctContentCount')) {
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
                throw 'The diagnostic must contain valid UTC enumeration times.'
            }
            $times[$name] = $parsed
        }
        if ($times.EnumerationFinishedAt -lt $times.EnumerationStartedAt) { throw 'The enumeration read window is reversed.' }
        $eventTime = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($root.GetProperty('EventTime').GetString(), [ref] $eventTime)) {
            throw 'The success envelope is missing a valid EventTime.'
        }
    }
    finally { $document.Dispose() }
}

# Reads one cancellable admin diagnostic and publishes validated JSON in the destination directory.
function Invoke-DirectoryVersionSizeDiagnosis {
    param([hashtable] $BoundParameters)

    $scope = @{}
    foreach ($name in @('OwnerId', 'OrganizationId', 'RepositoryId')) {
        $id = [guid]::Empty
        if (-not [guid]::TryParse($BoundParameters[$name], [ref] $id) -or $id -eq [guid]::Empty) {
            throw "$name must be a non-empty GUID."
        }
        $scope[$name] = $id.ToString()
    }
    $serverUri = $null
    if (-not [uri]::TryCreate($env:GRACE_SERVER_URI, [UriKind]::Absolute, [ref] $serverUri) -or
        $serverUri.Scheme -notin @('http', 'https')) { throw 'GRACE_SERVER_URI must contain the Grace Server HTTP(S) base URI.' }
    if ([string]::IsNullOrWhiteSpace($env:GRACE_TOKEN)) { throw 'GRACE_TOKEN must contain a SystemAdmin token.' }
    $destination = [IO.Path]::GetFullPath($BoundParameters.OutputPath)
    $parent = [IO.Path]::GetDirectoryName($destination)
    if (-not [IO.Directory]::Exists($parent) -or [IO.Directory]::Exists($destination)) {
        throw 'OutputPath must name a file in an existing directory.'
    }
    $response = Invoke-WebRequest -Uri ([uri]::new($serverUri, '/admin/directory-version-size/diagnose')) `
        -Method Post -Headers @{ Authorization = "Bearer $($env:GRACE_TOKEN)" } -ContentType 'application/json' `
        -Body ($scope | ConvertTo-Json -Compress) -SkipHttpErrorCheck -ConnectionTimeoutSeconds 0 -OperationTimeoutSeconds 0
    if ($response.StatusCode -ne 200) { throw "Grace Server returned HTTP $($response.StatusCode); no diagnostic was saved." }
    Test-DirectoryVersionSizeResponse -Json $response.Content -Scope $scope
    $temporary = Join-Path $parent ".$([IO.Path]::GetFileName($destination)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $response.Content, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $destination, $true)
    }
    finally { if ([IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary -Force } }
    Write-Host "DirectoryVersion declaration diagnostic saved to '$destination'."
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-DirectoryVersionSizeDiagnosis -BoundParameters $PSBoundParameters
        exit 0
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 4
    }
}
