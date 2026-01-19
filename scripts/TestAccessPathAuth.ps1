[CmdletBinding()]
param(
    [string]$BaseUri = "http://localhost:60164",
    [string]$AdminUserId = "test-admin",
    [string]$UserId = "test-user",
    [string]$IntruderUserId = "test-intruder"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:Failures = 0

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message =="
}

function New-CorrelationId {
    [Guid]::NewGuid().ToString()
}

function Invoke-GraceApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [hashtable]$Body,
        [string]$UserId
    )

    $uri = "{0}{1}" -f $BaseUri.TrimEnd("/"), $Path
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($UserId)) {
        $headers["x-grace-user-id"] = $UserId
    }

    $invokeParams = @{
        Method = $Method
        Uri = $uri
        Headers = $headers
        SkipHttpErrorCheck = $true
    }

    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 8
        $invokeParams["Body"] = $json
        $invokeParams["ContentType"] = "application/json"
    }

    try {
        $response = Invoke-WebRequest @invokeParams
    } catch {
        Write-Error "Request failed: $Method $uri. $($_.Exception.Message)"
        throw
    }

    [PSCustomObject]@{
        StatusCode = [int]$response.StatusCode
        Content = $response.Content
    }
}

function Assert-Status {
    param(
        [string]$Name,
        [int]$Actual,
        [int]$Expected
    )

    if ($Actual -ne $Expected) {
        $script:Failures++
        Write-Host "FAIL: $Name (expected $Expected, got $Actual)"
    } else {
        Write-Host "PASS: $Name ($Actual)"
    }
}

Write-Step "Preflight (TestAuth + bootstrap)"

$authMe = Invoke-GraceApi -Method "GET" -Path "/auth/me" -UserId $AdminUserId
Assert-Status "GET /auth/me with TestAuth" $authMe.StatusCode 200

$bootstrapBody = @{
    CorrelationId = New-CorrelationId
    ScopeKind = "system"
    PrincipalType = "User"
    PrincipalId = $AdminUserId
    RoleId = "SystemAdmin"
    Source = "manual-test"
    SourceDetail = "scripts/TestAccessPathAuth.ps1"
}

$bootstrap = Invoke-GraceApi -Method "POST" -Path "/access/grantRole" -Body $bootstrapBody -UserId $AdminUserId
Assert-Status "POST /access/grantRole (system admin bootstrap)" $bootstrap.StatusCode 200

$ownerId = [Guid]::NewGuid().ToString()
$organizationId = [Guid]::NewGuid().ToString()
$repositoryId = [Guid]::NewGuid().ToString()
$branchId = [Guid]::NewGuid().ToString()
$path = "src/README.md"

$repoScope = @{
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    ScopeKind = "repository"
}

Write-Step "listRoles"

$listRolesNoAuth = Invoke-GraceApi -Method "GET" -Path "/access/listRoles"
Assert-Status "GET /access/listRoles (no auth)" $listRolesNoAuth.StatusCode 401

$listRolesAuth = Invoke-GraceApi -Method "GET" -Path "/access/listRoles" -UserId $UserId
Assert-Status "GET /access/listRoles (auth)" $listRolesAuth.StatusCode 200

Write-Step "grantRole"

$grantRoleBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    ScopeKind = "repository"
    PrincipalType = "User"
    PrincipalId = $UserId
    RoleId = "RepoAdmin"
    Source = "manual-test"
    SourceDetail = "scripts/TestAccessPathAuth.ps1"
}

$grantRoleNoAuth = Invoke-GraceApi -Method "POST" -Path "/access/grantRole" -Body $grantRoleBody
Assert-Status "POST /access/grantRole (no auth)" $grantRoleNoAuth.StatusCode 401

$grantRoleForbidden = Invoke-GraceApi -Method "POST" -Path "/access/grantRole" -Body $grantRoleBody -UserId $IntruderUserId
Assert-Status "POST /access/grantRole (unauthorized)" $grantRoleForbidden.StatusCode 403

$grantRoleOk = Invoke-GraceApi -Method "POST" -Path "/access/grantRole" -Body $grantRoleBody -UserId $AdminUserId
Assert-Status "POST /access/grantRole (admin)" $grantRoleOk.StatusCode 200

$grantRoleInvalid = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    ScopeKind = "repository"
    PrincipalType = "User"
    PrincipalId = $UserId
    RoleId = "NotARole"
    Source = "manual-test"
    SourceDetail = "scripts/TestAccessPathAuth.ps1"
}

$grantRoleBad = Invoke-GraceApi -Method "POST" -Path "/access/grantRole" -Body $grantRoleInvalid -UserId $AdminUserId
Assert-Status "POST /access/grantRole (invalid role)" $grantRoleBad.StatusCode 400

Write-Step "listRoleAssignments"

$listAssignmentsBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    ScopeKind = "repository"
    PrincipalType = ""
    PrincipalId = ""
}

$listAssignmentsNoAuth = Invoke-GraceApi -Method "POST" -Path "/access/listRoleAssignments" -Body $listAssignmentsBody
Assert-Status "POST /access/listRoleAssignments (no auth)" $listAssignmentsNoAuth.StatusCode 401

$listAssignmentsForbidden = Invoke-GraceApi -Method "POST" -Path "/access/listRoleAssignments" -Body $listAssignmentsBody -UserId $IntruderUserId
Assert-Status "POST /access/listRoleAssignments (unauthorized)" $listAssignmentsForbidden.StatusCode 403

$listAssignmentsOk = Invoke-GraceApi -Method "POST" -Path "/access/listRoleAssignments" -Body $listAssignmentsBody -UserId $AdminUserId
Assert-Status "POST /access/listRoleAssignments (admin)" $listAssignmentsOk.StatusCode 200

Write-Step "revokeRole"

$revokeRoleBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    ScopeKind = "repository"
    PrincipalType = "User"
    PrincipalId = $UserId
    RoleId = "RepoAdmin"
}

$revokeRoleForbidden = Invoke-GraceApi -Method "POST" -Path "/access/revokeRole" -Body $revokeRoleBody -UserId $IntruderUserId
Assert-Status "POST /access/revokeRole (unauthorized)" $revokeRoleForbidden.StatusCode 403

$revokeRoleOk = Invoke-GraceApi -Method "POST" -Path "/access/revokeRole" -Body $revokeRoleBody -UserId $AdminUserId
Assert-Status "POST /access/revokeRole (admin)" $revokeRoleOk.StatusCode 200

$revokeRoleInvalid = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    ScopeKind = "repository"
    PrincipalType = "User"
    PrincipalId = $UserId
    RoleId = "NotARole"
}

$revokeRoleBad = Invoke-GraceApi -Method "POST" -Path "/access/revokeRole" -Body $revokeRoleInvalid -UserId $AdminUserId
Assert-Status "POST /access/revokeRole (invalid role)" $revokeRoleBad.StatusCode 400

Write-Step "path permission management"

$upsertBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Path = $path
    ClaimPermissions = @(
        @{
            Claim = "dev-claim"
            DirectoryPermission = "Read"
        }
    )
}

$upsertNoAuth = Invoke-GraceApi -Method "POST" -Path "/access/upsertPathPermission" -Body $upsertBody
Assert-Status "POST /access/upsertPathPermission (no auth)" $upsertNoAuth.StatusCode 401

$upsertForbidden = Invoke-GraceApi -Method "POST" -Path "/access/upsertPathPermission" -Body $upsertBody -UserId $IntruderUserId
Assert-Status "POST /access/upsertPathPermission (unauthorized)" $upsertForbidden.StatusCode 403

$upsertOk = Invoke-GraceApi -Method "POST" -Path "/access/upsertPathPermission" -Body $upsertBody -UserId $AdminUserId
Assert-Status "POST /access/upsertPathPermission (admin)" $upsertOk.StatusCode 200

$listPathBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Path = $path
}

$listPathForbidden = Invoke-GraceApi -Method "POST" -Path "/access/listPathPermissions" -Body $listPathBody -UserId $IntruderUserId
Assert-Status "POST /access/listPathPermissions (unauthorized)" $listPathForbidden.StatusCode 403

$listPathOk = Invoke-GraceApi -Method "POST" -Path "/access/listPathPermissions" -Body $listPathBody -UserId $AdminUserId
Assert-Status "POST /access/listPathPermissions (admin)" $listPathOk.StatusCode 200

$removePathBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Path = $path
}

$removePathForbidden = Invoke-GraceApi -Method "POST" -Path "/access/removePathPermission" -Body $removePathBody -UserId $IntruderUserId
Assert-Status "POST /access/removePathPermission (unauthorized)" $removePathForbidden.StatusCode 403

$removePathOk = Invoke-GraceApi -Method "POST" -Path "/access/removePathPermission" -Body $removePathBody -UserId $AdminUserId
Assert-Status "POST /access/removePathPermission (admin)" $removePathOk.StatusCode 200

Write-Step "checkPermission"

$checkRepoBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Operation = "RepoRead"
    ResourceKind = "repository"
}

$checkNoAuth = Invoke-GraceApi -Method "POST" -Path "/access/checkPermission" -Body $checkRepoBody
Assert-Status "POST /access/checkPermission (no auth)" $checkNoAuth.StatusCode 401

$checkSelf = Invoke-GraceApi -Method "POST" -Path "/access/checkPermission" -Body $checkRepoBody -UserId $UserId
Assert-Status "POST /access/checkPermission (self, no principal specified)" $checkSelf.StatusCode 200

$checkSelfExplicit = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Operation = "RepoRead"
    ResourceKind = "repository"
    PrincipalType = "User"
    PrincipalId = $UserId
}

$checkSelfExplicitResult = Invoke-GraceApi -Method "POST" -Path "/access/checkPermission" -Body $checkSelfExplicit -UserId $UserId
Assert-Status "POST /access/checkPermission (self, explicit principal)" $checkSelfExplicitResult.StatusCode 200

$checkOtherForbidden = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Operation = "RepoRead"
    ResourceKind = "repository"
    PrincipalType = "User"
    PrincipalId = $AdminUserId
}

$checkOtherForbiddenResult = Invoke-GraceApi -Method "POST" -Path "/access/checkPermission" -Body $checkOtherForbidden -UserId $UserId
Assert-Status "POST /access/checkPermission (other principal, unauthorized)" $checkOtherForbiddenResult.StatusCode 403

$checkOtherAllowed = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Operation = "RepoRead"
    ResourceKind = "repository"
    PrincipalType = "User"
    PrincipalId = $IntruderUserId
}

$checkOtherAllowedResult = Invoke-GraceApi -Method "POST" -Path "/access/checkPermission" -Body $checkOtherAllowed -UserId $AdminUserId
Assert-Status "POST /access/checkPermission (other principal, admin)" $checkOtherAllowedResult.StatusCode 200

$checkPathBody = @{
    CorrelationId = New-CorrelationId
    OwnerId = $ownerId
    OrganizationId = $organizationId
    RepositoryId = $repositoryId
    Operation = "PathRead"
    ResourceKind = "path"
    Path = $path
}

$checkPathSelf = Invoke-GraceApi -Method "POST" -Path "/access/checkPermission" -Body $checkPathBody -UserId $UserId
Assert-Status "POST /access/checkPermission (path, self)" $checkPathSelf.StatusCode 200

Write-Step "Summary"

if ($script:Failures -gt 0) {
    Write-Host "Completed with $script:Failures failure(s)."
    exit 1
}

Write-Host "All access authorization checks passed."
