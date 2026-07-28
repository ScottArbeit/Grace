[CmdletBinding(DefaultParameterSetName = 'Reference')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Reference')]
    [ValidateNotNullOrEmpty()]
    [string] $ReferenceId,

    [Parameter(Mandatory, ParameterSetName = 'DirectoryVersion')]
    [ValidateNotNullOrEmpty()]
    [string] $DirectoryVersionId,

    [Parameter(ParameterSetName = 'Reference')]
    [Parameter(ParameterSetName = 'DirectoryVersion')]
    [Parameter(Mandatory, ParameterSetName = 'Counter')]
    [ValidateNotNullOrEmpty()]
    [string] $RepositoryId,

    [Parameter(Mandatory, ParameterSetName = 'Counter')]
    [ValidateNotNullOrEmpty()]
    [string] $StoragePoolId,

    [Parameter(Mandatory, ParameterSetName = 'Counter')]
    [ValidateNotNullOrEmpty()]
    [string] $ManifestAddress,

    [Parameter(Mandatory, ParameterSetName = 'Operation')]
    [ValidateNotNullOrEmpty()]
    [string] $RepositoryContentCounterOperationId,

    [Parameter(Mandatory)]
    [ValidateRange(1, 5000)]
    [int] $MaxRelationships,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Test-NonEmptyGuid {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Value
    )

    $parsed = [guid]::Empty

    if (-not [guid]::TryParse($Value, [ref] $parsed) -or $parsed -eq [guid]::Empty) {
        throw "$Name must be a non-empty GUID."
    }
}

function Get-ValidatedOutputPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)

    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        throw "OutputPath must name a JSON file, not a directory: $fullPath"
    }

    $parent = Split-Path -Parent $fullPath

    if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "OutputPath parent directory does not exist: $parent"
    }

    return $fullPath
}

function Test-ReportSha256 {
    param(
        [Parameter(Mandatory)]
        [string] $Json
    )

    try {
        $document = [Text.Json.Nodes.JsonNode]::Parse($Json)
        $reportedHash = $document['ReportSha256'].GetValue[string]()

        if ([string]::IsNullOrWhiteSpace($reportedHash) -or $reportedHash.Length -ne 64) {
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

function Get-DiagnosisExitCode {
    param(
        [Parameter(Mandatory)]
        [string] $Outcome
    )

    switch ($Outcome.ToLowerInvariant()) {
        'verifiedcomplete' { return 0 }
        'incompleteretain' { return 2 }
        'failedretain' { return 3 }
        default { throw "The server returned an unknown diagnosis outcome: $Outcome" }
    }
}

function Invoke-ManifestContributionDiagnosis {
    param(
        [Parameter(Mandatory)]
        [hashtable] $BoundParameters,

        [Parameter(Mandatory)]
        [string] $ParameterSetName
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

    switch ($ParameterSetName) {
        'Reference' {
            Test-NonEmptyGuid -Name 'ReferenceId' -Value $BoundParameters.ReferenceId

            if ($BoundParameters.ContainsKey('RepositoryId')) {
                Test-NonEmptyGuid -Name 'RepositoryId' -Value $BoundParameters.RepositoryId
            }
        }
        'DirectoryVersion' {
            Test-NonEmptyGuid -Name 'DirectoryVersionId' -Value $BoundParameters.DirectoryVersionId

            if ($BoundParameters.ContainsKey('RepositoryId')) {
                Test-NonEmptyGuid -Name 'RepositoryId' -Value $BoundParameters.RepositoryId
            }
        }
        'Counter' {
            Test-NonEmptyGuid -Name 'RepositoryId' -Value $BoundParameters.RepositoryId
        }
        'Operation' {
            if ([string]::IsNullOrWhiteSpace($BoundParameters.RepositoryContentCounterOperationId)) {
                throw 'RepositoryContentCounterOperationId must not be empty.'
            }
        }
        default {
            throw "Unsupported selector parameter set: $ParameterSetName"
        }
    }

    $validatedOutputPath = Get-ValidatedOutputPath -Path $BoundParameters.OutputPath

    $body = @{
        ReferenceId = if ($BoundParameters.ContainsKey('ReferenceId')) { $BoundParameters.ReferenceId } else { '' }
        DirectoryVersionId = if ($BoundParameters.ContainsKey('DirectoryVersionId')) { $BoundParameters.DirectoryVersionId } else { '' }
        RepositoryId = if ($BoundParameters.ContainsKey('RepositoryId')) { $BoundParameters.RepositoryId } else { '' }
        StoragePoolId = if ($BoundParameters.ContainsKey('StoragePoolId')) { $BoundParameters.StoragePoolId } else { '' }
        ManifestAddress = if ($BoundParameters.ContainsKey('ManifestAddress')) { $BoundParameters.ManifestAddress } else { '' }
        RepositoryContentCounterOperationId =
            if ($BoundParameters.ContainsKey('RepositoryContentCounterOperationId')) {
                $BoundParameters.RepositoryContentCounterOperationId
            }
            else {
                ''
            }
        MaxRelationships = $BoundParameters.MaxRelationships
    } | ConvertTo-Json -Compress

    $routeUri = [uri]::new($serverUri, '/admin/manifest-contribution/diagnose')
    $headers = @{ Authorization = "Bearer $($env:GRACE_TOKEN)" }
    $response = Invoke-WebRequest -Uri $routeUri -Method Post -Headers $headers -ContentType 'application/json' -Body $body -SkipHttpErrorCheck

    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Grace Server returned HTTP $($response.StatusCode) for manifest contribution diagnosis."
    }

    $json = $response.Content

    if (-not (Test-ReportSha256 -Json $json)) {
        throw 'The diagnosis report SHA-256 is missing or does not match the complete response.'
    }

    $report = $json | ConvertFrom-Json
    $exitCode = Get-DiagnosisExitCode -Outcome ([string] $report.Outcome)
    $temporaryPath = Join-Path (Split-Path -Parent $validatedOutputPath) ".$([IO.Path]::GetFileName($validatedOutputPath)).$([guid]::NewGuid().ToString('N')).tmp"

    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $validatedOutputPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    Write-Host "Manifest contribution diagnosis wrote a verified report to '$validatedOutputPath'."
    return $exitCode
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $result = Invoke-ManifestContributionDiagnosis -BoundParameters $PSBoundParameters -ParameterSetName $PSCmdlet.ParameterSetName
        exit $result
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 4
    }
}
