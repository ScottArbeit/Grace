[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ExpectedReportSha256,

    [switch] $Execute,

    [ValidateNotNullOrEmpty()]
    [string] $OutputPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Test-ManifestContributionReportSha256 {
    param(
        [Parameter(Mandatory)]
        [string] $Json
    )

    try {
        $document = [Text.Json.Nodes.JsonNode]::Parse($Json)
        $reportedHash = $document['ReportSha256'].GetValue[string]()

        if ($reportedHash -notmatch '^[0-9a-fA-F]{64}$') {
            return $false
        }

        $document.AsObject().Remove('ReportSha256') | Out-Null
        $unsignedJson = $document.ToJsonString()
        $bytes = [Text.Encoding]::UTF8.GetBytes($unsignedJson)
        $computedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        return [string]::Equals($reportedHash, $computedHash, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Get-RepairExitCode {
    param(
        [Parameter(Mandatory)]
        [string] $Outcome
    )

    switch ($Outcome.ToLowerInvariant()) {
        'verifiedcomplete' { return 0 }
        'incompleteretain' { return 2 }
        'failedretain' { return 3 }
        default { throw "The server returned an unknown repair outcome: $Outcome" }
    }
}

function Get-ValidatedOptionalOutputPath {
    param(
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath

    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        throw "OutputPath must name a JSON file, not a directory: $fullPath"
    }

    if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "OutputPath parent directory does not exist: $parent"
    }

    return $fullPath
}

function Write-RepairReportAtomically {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Json
    )

    $temporaryPath = Join-Path (Split-Path -Parent $Path) ".$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"

    try {
        [IO.File]::WriteAllText($temporaryPath, $Json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Invoke-ManifestContributionRepair {
    param(
        [Parameter(Mandatory)]
        [hashtable] $BoundParameters
    )

    if ([string]::IsNullOrWhiteSpace($env:GRACE_SERVER_URI)) {
        throw 'GRACE_SERVER_URI must contain the Grace Server base URI.'
    }

    $serverUri = $null

    if (-not [uri]::TryCreate($env:GRACE_SERVER_URI, [UriKind]::Absolute, [ref] $serverUri) -or
        $serverUri.Scheme -notin @('http', 'https')) {
        throw 'GRACE_SERVER_URI must be an absolute HTTP or HTTPS URI.'
    }

    if ([string]::IsNullOrWhiteSpace($env:GRACE_TOKEN)) {
        throw 'GRACE_TOKEN must contain a SystemAdmin bearer token.'
    }

    if ($BoundParameters.ExpectedReportSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'ExpectedReportSha256 must contain exactly 64 hexadecimal characters.'
    }

    $validatedReportPath = [IO.Path]::GetFullPath($BoundParameters.ReportPath)

    if (-not (Test-Path -LiteralPath $validatedReportPath -PathType Leaf)) {
        throw "ReportPath does not exist or is not a file: $validatedReportPath"
    }

    $reportJson = [IO.File]::ReadAllText($validatedReportPath, [Text.Encoding]::UTF8)

    if (-not (Test-ManifestContributionReportSha256 -Json $reportJson)) {
        throw 'The diagnosis report SHA-256 is missing or does not match the complete report.'
    }

    $diagnosis = $reportJson | ConvertFrom-Json

    if ($diagnosis.SchemaVersion -ne 'grace.manifest-contribution-diagnosis.v1') {
        throw "The diagnosis report schema is not supported: $($diagnosis.SchemaVersion)"
    }

    if (-not [string]::Equals(
            [string] $diagnosis.ReportSha256,
            $BoundParameters.ExpectedReportSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ExpectedReportSha256 does not match the diagnosis report.'
    }

    $validatedOutputPath =
        if ($BoundParameters.ContainsKey('OutputPath')) {
            Get-ValidatedOptionalOutputPath -Path $BoundParameters.OutputPath
        }
        else {
            $null
        }

    $body = @{
        ReportJson = $reportJson
        ExpectedReportSha256 = $BoundParameters.ExpectedReportSha256
        Execute = [bool] $BoundParameters.Execute
    } | ConvertTo-Json -Compress

    $routeUri = [uri]::new($serverUri, '/admin/manifest-contribution/repair')
    $headers = @{ Authorization = "Bearer $($env:GRACE_TOKEN)" }
    $response = Invoke-WebRequest -Uri $routeUri -Method Post -Headers $headers -ContentType 'application/json' -Body $body -SkipHttpErrorCheck

    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Grace Server returned HTTP $($response.StatusCode) for manifest contribution repair."
    }

    $repairJson = [string] $response.Content
    $repair = $repairJson | ConvertFrom-Json

    if ($repair.SchemaVersion -ne 'grace.manifest-contribution-repair.v1' -or
        -not [string]::Equals(
            [string] $repair.DiagnosisReportSha256,
            $BoundParameters.ExpectedReportSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        [bool] $repair.Execute -ne [bool] $BoundParameters.Execute) {
        throw 'The server returned a repair report that does not match the validated invocation.'
    }

    if ($null -ne $validatedOutputPath) {
        Write-RepairReportAtomically -Path $validatedOutputPath -Json $repairJson
    }

    [Console]::Out.WriteLine($repairJson)
    return Get-RepairExitCode -Outcome ([string] $repair.Outcome)
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $result = Invoke-ManifestContributionRepair -BoundParameters $PSBoundParameters
        exit $result
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 4
    }
}
