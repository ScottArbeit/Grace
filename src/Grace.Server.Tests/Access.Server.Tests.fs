namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http

/// Covers access bootstrap enabled scenarios.
[<NonParallelizable>]
type AccessBootstrapEnabledTests() =

    /// Builds a deterministic owner for integration setup fixture for the server integration access assertions.
    let createOwner (client: HttpClient) =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let ownerParameters = Parameters.Owner.CreateOwnerParameters()
            ownerParameters.OwnerId <- ownerId
            ownerParameters.OwnerName <- $"BootstrapOwner{Guid.NewGuid():N}"
            ownerParameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/owner/create", createJsonContent ownerParameters)
            response.EnsureSuccessStatusCode() |> ignore

            return ownerId
        }

    /// Builds create grant role parameters for route calls.
    let createGrantRoleParameters (roleId: string) (principalId: string) =
        let parameters = Parameters.Access.GrantRoleParameters()
        parameters.ScopeKind <- "system"
        parameters.PrincipalType <- "User"
        parameters.PrincipalId <- principalId
        parameters.RoleId <- roleId
        parameters.Source <- "test"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds a deterministic client for integration setup fixture for the server integration access assertions.
    let createClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Verifies the bootstrap seeds system admin when configured scenario.
    [<Test>]
    member _.BootstrapSeedsSystemAdminWhenConfigured() =
        task {
            use client = createClient testUserId
            let! _ownerId = createOwner client

            let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/authorize/grant-role", createJsonContent parameters)

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }


namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http

/// Covers access bootstrap scenarios.
[<NonParallelizable>]
type AccessBootstrapTests() =

    /// Builds a deterministic owner for integration setup fixture for the server integration access assertions.
    let createOwner (client: HttpClient) =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let ownerParameters = Parameters.Owner.CreateOwnerParameters()
            ownerParameters.OwnerId <- ownerId
            ownerParameters.OwnerName <- $"BootstrapOwner{Guid.NewGuid():N}"
            ownerParameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/owner/create", createJsonContent ownerParameters)
            response.EnsureSuccessStatusCode() |> ignore

            return ownerId
        }

    /// Builds create grant role parameters for route calls.
    let createGrantRoleParameters (roleId: string) (principalId: string) =
        let parameters = Parameters.Access.GrantRoleParameters()
        parameters.ScopeKind <- "system"
        parameters.PrincipalType <- "User"
        parameters.PrincipalId <- principalId
        parameters.RoleId <- roleId
        parameters.Source <- "test"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds a deterministic client for integration setup fixture for the server integration access assertions.
    let createClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Verifies the bootstrap disabled by default returns forbidden scenario.
    [<Test>]
    member _.BootstrapDisabledByDefaultReturnsForbidden() =
        task {
            use client = createClient $"{Guid.NewGuid()}"
            let! _ownerId = createOwner Client

            let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/authorize/grant-role", createJsonContent parameters)

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }


namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json

/// Covers access check permission scenarios.
[<Parallelizable(ParallelScope.All)>]
type AccessCheckPermissionTests() =

    /// Builds a deterministic client for integration setup fixture for the server integration access assertions.
    let createClient (userId: string) (groups: string list) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)

        if not groups.IsEmpty then
            client.DefaultRequestHeaders.Add("x-grace-groups", String.Join(";", groups))

        client

    /// Defines check permission behavior for the surrounding tests used by the server integration access scenario.
    let checkPermissionAsync (client: HttpClient) ownerId organizationId repositoryId resourceKind operation principalType principalId =
        task {
            let parameters = Parameters.Access.CheckPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ResourceKind <- resourceKind
            parameters.Operation <- operation
            parameters.Path <- ""
            parameters.PrincipalType <- principalType
            parameters.PrincipalId <- principalId
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/check-permission", createJsonContent parameters)
        }

    /// Verifies the self check without principal specified returns ok scenario.
    [<Test>]
    member _.SelfCheckWithoutPrincipalSpecifiedReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepositoryRead" "" ""
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the explicit self principal returns ok scenario.
    [<Test>]
    member _.ExplicitSelfPrincipalReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepositoryRead" "User" userId

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the group principal member returns ok scenario.
    [<Test>]
    member _.GroupPrincipalMemberReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            let groupId = $"{Guid.NewGuid()}"
            use client = createClient userId [ groupId ]

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepositoryRead" "Group" groupId

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the other principal requires admin scenario.
    [<Test>]
    member _.OtherPrincipalRequiresAdmin() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response =
                checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepositoryRead" "User" $"{Guid.NewGuid()}"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }


namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json

/// Covers access control security scenarios.
[<Parallelizable(ParallelScope.All)>]
type AccessControlSecurityTests() =

    /// Builds a deterministic client with user ID for integration setup fixture for the server integration access assertions.
    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Grants role needed by authorization-sensitive tests.
    let grantRoleAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId principalId roleId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/grant-role", createJsonContent parameters)
        }

    /// Revokes role during authorization-sensitive tests.
    let revokeRoleAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId principalId roleId =
        task {
            let parameters = Parameters.Access.RevokeRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/revoke-role", createJsonContent parameters)
        }

    /// Lists role assignments from the running test server.
    let listRoleAssignmentsAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId =
        task {
            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.ScopeKind <- scopeKind
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/list-role-assignments", createJsonContent parameters)
        }

    /// Verifies the system scope requires system admin scenario.
    [<Test>]
    member _.SystemScopeRequiresSystemAdmin() =
        task {
            let adminUser = $"{Guid.NewGuid()}"
            let nonAdminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "system" "" "" "" "" adminUser "SystemAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser
            use nonAdminClient = createClientWithUserId nonAdminUser

            let! allowed = grantRoleAsync adminClient "system" "" "" "" "" $"{Guid.NewGuid()}" "SystemAdmin"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync nonAdminClient "system" "" "" "" "" $"{Guid.NewGuid()}" "SystemAdmin"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the owner scope boundaries enforced scenario.
    [<Test>]
    member _.OwnerScopeBoundariesEnforced() =
        task {
            let ownerA = $"{Guid.NewGuid()}"
            let ownerB = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "owner" ownerA "" "" "" adminUser "OwnerAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser

            let! allowed = grantRoleAsync adminClient "owner" ownerA "" "" "" $"{Guid.NewGuid()}" "OwnerReader"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync adminClient "owner" ownerB "" "" "" $"{Guid.NewGuid()}" "OwnerReader"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! revokeForbidden = revokeRoleAsync adminClient "owner" ownerB "" "" "" $"{Guid.NewGuid()}" "OwnerReader"
            Assert.That(revokeForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! listAllowed = listRoleAssignmentsAsync adminClient "owner" ownerA "" "" ""
            Assert.That(listAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listForbidden = listRoleAssignmentsAsync adminClient "owner" ownerB "" "" ""
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the organization scope boundaries enforced scenario.
    [<Test>]
    member _.OrganizationScopeBoundariesEnforced() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgA = $"{Guid.NewGuid()}"
            let orgB = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "org" ownerId orgA "" "" adminUser "OrganizationAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser

            let! allowed = grantRoleAsync adminClient "org" ownerId orgA "" "" $"{Guid.NewGuid()}" "OrganizationReader"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync adminClient "org" ownerId orgB "" "" $"{Guid.NewGuid()}" "OrganizationReader"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! revokeForbidden = revokeRoleAsync adminClient "org" ownerId orgB "" "" $"{Guid.NewGuid()}" "OrganizationReader"
            Assert.That(revokeForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! listAllowed = listRoleAssignmentsAsync adminClient "org" ownerId orgA "" ""
            Assert.That(listAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listForbidden = listRoleAssignmentsAsync adminClient "org" ownerId orgB "" ""
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the repository scope boundaries enforced scenario.
    [<Test>]
    member _.RepositoryScopeBoundariesEnforced() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoA = $"{Guid.NewGuid()}"
            let repoB = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "repo" ownerId orgId repoA "" adminUser "RepositoryAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser

            let! allowed = grantRoleAsync adminClient "repo" ownerId orgId repoA "" $"{Guid.NewGuid()}" "RepositoryReader"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync adminClient "repo" ownerId orgId repoB "" $"{Guid.NewGuid()}" "RepositoryReader"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! revokeForbidden = revokeRoleAsync adminClient "repo" ownerId orgId repoB "" $"{Guid.NewGuid()}" "RepositoryReader"
            Assert.That(revokeForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! listAllowed = listRoleAssignmentsAsync adminClient "repo" ownerId orgId repoA ""
            Assert.That(listAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listForbidden = listRoleAssignmentsAsync adminClient "repo" ownerId orgId repoB ""
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the branch scope boundaries enforced scenario.
    [<Test>]
    member _.BranchScopeBoundariesEnforced() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoId = $"{Guid.NewGuid()}"
            let branchA = $"{Guid.NewGuid()}"
            let branchB = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "branch" ownerId orgId repoId branchA adminUser "BranchAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser

            let! allowed = grantRoleAsync adminClient "branch" ownerId orgId repoId branchA $"{Guid.NewGuid()}" "BranchReader"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync adminClient "branch" ownerId orgId repoId branchB $"{Guid.NewGuid()}" "BranchReader"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! revokeForbidden = revokeRoleAsync adminClient "branch" ownerId orgId repoId branchB $"{Guid.NewGuid()}" "BranchReader"
            Assert.That(revokeForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! listAllowed = listRoleAssignmentsAsync adminClient "branch" ownerId orgId repoId branchA
            Assert.That(listAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listForbidden = listRoleAssignmentsAsync adminClient "branch" ownerId orgId repoId branchB
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the privilege escalation prevention scenario.
    [<Test>]
    member _.PrivilegeEscalationPrevention() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoId = $"{Guid.NewGuid()}"

            let repoAdminUser = $"{Guid.NewGuid()}"
            let orgAdminUser = $"{Guid.NewGuid()}"
            let repoWriterUser = $"{Guid.NewGuid()}"

            let! grantRepositoryAdmin = grantRoleAsync Client "repo" ownerId orgId repoId "" repoAdminUser "RepositoryAdmin"
            Assert.That(grantRepositoryAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantOrganizationAdmin = grantRoleAsync Client "org" ownerId orgId "" "" orgAdminUser "OrganizationAdmin"
            Assert.That(grantOrganizationAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantRepositoryWrite = grantRoleAsync Client "repo" ownerId orgId repoId "" repoWriterUser "RepositoryContributor"
            Assert.That(grantRepositoryWrite.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use repoAdminClient = createClientWithUserId repoAdminUser
            use orgAdminClient = createClientWithUserId orgAdminUser
            use repoWriterClient = createClientWithUserId repoWriterUser

            let! repoAdminCannotGrantOrg = grantRoleAsync repoAdminClient "org" ownerId orgId "" "" $"{Guid.NewGuid()}" "OrganizationAdmin"

            Assert.That(repoAdminCannotGrantOrg.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! orgAdminCannotGrantOwner = grantRoleAsync orgAdminClient "owner" ownerId "" "" "" $"{Guid.NewGuid()}" "OwnerAdmin"

            Assert.That(orgAdminCannotGrantOwner.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! repoWriterCannotGrantRepositoryAdmin = grantRoleAsync repoWriterClient "repo" ownerId orgId repoId "" $"{Guid.NewGuid()}" "RepositoryAdmin"

            Assert.That(repoWriterCannotGrantRepositoryAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the grant role rejects invalid role scenario.
    [<Test>]
    member _.GrantRoleRejectsInvalidRole() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoId = $"{Guid.NewGuid()}"

            let! response = grantRoleAsync Client "repo" ownerId orgId repoId "" $"{Guid.NewGuid()}" "NotARole"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the grant role rejects role scope mismatch scenario.
    [<Test>]
    member _.GrantRoleRejectsRoleScopeMismatch() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"

            let! response = grantRoleAsync Client "org" ownerId orgId "" "" $"{Guid.NewGuid()}" "RepositoryAdmin"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }


namespace Grace.Server.Tests

open FSharp.Control
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading.Tasks

/// Groups shared helpers for access storage test data.
module AccessStorageTestData =
    /// Builds a deterministic file version for integration setup fixture for the server integration access assertions.
    let createFileVersion (relativePath: string) =
        FileVersion.CreateWithHashes
            (RelativePath relativePath)
            (Sha256Hash "805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243")
            (Blake3Hash "9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d")
            String.Empty
            false
            1L

/// Covers access control scenarios.
[<Parallelizable(ParallelScope.All)>]
type AccessControl() =

    /// Builds a deterministic organization for integration setup fixture for the server integration access assertions.
    let createOrganizationAsync (client: HttpClient) =
        task {
            let organizationId = $"{Guid.NewGuid()}"

            let parameters = Parameters.Organization.CreateOrganizationParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.OrganizationName <- $"AuthzOrg{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/organization/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            return organizationId
        }

    /// Builds a deterministic repository for integration setup fixture for the server integration access assertions.
    let createRepositoryAsync (client: HttpClient) (organizationId: string) =
        task {
            let repositoryId = $"{Guid.NewGuid()}"

            let parameters = Parameters.Repository.CreateRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"AuthzRepo{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/repository/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            return repositoryId
        }

    /// Grants role needed by authorization-sensitive tests.
    let grantRoleAsync (client: HttpClient) ownerId organizationId repositoryId scopeKind roleId principalId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/authorize/grant-role", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    /// Defines upsert path permission behavior for the surrounding tests used by the server integration access scenario.
    let upsertPathPermissionAsync (client: HttpClient) ownerId organizationId repositoryId path claimPermissions =
        task {
            let parameters = Parameters.Access.UpsertPathPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Path <- path
            parameters.CorrelationId <- generateCorrelationId ()

            for (claim, permission) in claimPermissions do
                let claimPermission = Parameters.Access.ClaimPermissionParameters()
                claimPermission.Claim <- claim
                claimPermission.DirectoryPermission <- permission
                parameters.ClaimPermissions.Add(claimPermission)

            let! response = client.PostAsync("/authorize/upsert-path-permission", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    /// Defines check permission behavior for the surrounding tests used by the server integration access scenario.
    let checkPermissionAsync (client: HttpClient) ownerId organizationId repositoryId resourceKind operation path =
        task {
            let parameters = Parameters.Access.CheckPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ResourceKind <- resourceKind
            parameters.Operation <- operation
            parameters.Path <- path
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/authorize/check-permission", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PermissionCheckResult>> response
            return returnValue.ReturnValue
        }

    /// Revokes role during authorization-sensitive tests.
    let revokeRoleAsync (client: HttpClient) ownerId organizationId repositoryId scopeKind roleId principalId =
        task {
            let parameters = Parameters.Access.RevokeRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/authorize/revoke-role", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    /// Lists role assignments from the running test server.
    let listRoleAssignmentsAsync (client: HttpClient) ownerId organizationId repositoryId scopeKind =
        task {
            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ScopeKind <- scopeKind
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/authorize/list-role-assignments", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)
            let returnValue = deserialize<GraceReturnValue<RoleAssignment list>> responseText
            return returnValue.ReturnValue
        }

    /// Builds a deterministic client with claims for integration setup fixture for the server integration access assertions.
    let createClientWithClaims (claims: string list) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", testUserId)

        if not (List.isEmpty claims) then
            client.DefaultRequestHeaders.Add("x-grace-claims", String.Join(";", claims))

        client

    /// Builds a deterministic client with user ID for integration setup fixture for the server integration access assertions.
    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Verifies the protected endpoints require authentication scenario.
    [<Test>]
    member _.ProtectedEndpointsRequireAuthentication() =
        task {
            use unauthenticatedClient = new HttpClient()
            unauthenticatedClient.BaseAddress <- Client.BaseAddress

            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[0]
            parameters.Description <- "Authz test description"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = unauthenticatedClient.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    /// Verifies the role inheritance resolves permissions across organizations scenario.
    [<Test>]
    member _.RoleInheritanceResolvesPermissionsAcrossOrganizations() =
        task {
            let! orgA = createOrganizationAsync Client
            let! orgB = createOrganizationAsync Client
            let! repoA = createRepositoryAsync Client orgA
            let! repoB = createRepositoryAsync Client orgB

            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            do! grantRoleAsync Client ownerId orgA "" "org" "OrganizationAdmin" nonAdminUserId
            do! grantRoleAsync Client ownerId orgB "" "org" "OrganizationReader" nonAdminUserId

            let! repoAWrite = checkPermissionAsync nonAdminClient ownerId orgA repoA "repo" "RepositoryWrite" ""
            let! repoBWrite = checkPermissionAsync nonAdminClient ownerId orgB repoB "repo" "RepositoryWrite" ""
            let! repoBRead = checkPermissionAsync nonAdminClient ownerId orgB repoB "repo" "RepositoryRead" ""

            match repoAWrite with
            | Allowed _ -> ()
            | Denied reason -> Assert.Fail($"Expected repoA write to be allowed. {reason}")

            match repoBWrite with
            | Denied _ -> ()
            | Allowed reason -> Assert.Fail($"Expected repoB write to be denied. {reason}")

            match repoBRead with
            | Allowed _ -> ()
            | Denied reason -> Assert.Fail($"Expected repoB read to be allowed. {reason}")
        }

    /// Verifies the grant list revoke updates stored assignments and permission check scenario.
    [<Test>]
    member _.GrantListRevokeUpdatesStoredAssignmentsAndPermissionCheck() =
        task {
            let! organizationId = createOrganizationAsync Client
            let! repositoryId = createRepositoryAsync Client organizationId
            let principalId = $"{Guid.NewGuid()}"

            do! grantRoleAsync Client ownerId organizationId "" "org" "OrganizationAdmin" testUserId
            do! grantRoleAsync Client ownerId organizationId repositoryId "repo" "RepositoryReader" principalId

            let! afterGrant = listRoleAssignmentsAsync Client ownerId organizationId repositoryId "repo"

            let granted =
                afterGrant
                |> List.tryFind (fun assignment ->
                    assignment.Principal.PrincipalId = principalId
                    && assignment.RoleId = "RepositoryReader"
                    && assignment.Source = "test")

            Assert.That(granted.IsSome, Is.True)

            match granted.Value.Scope with
            | Scope.Repository (storedOwnerId, storedOrganizationId, storedRepositoryId) ->
                Assert.That(storedOwnerId, Is.EqualTo(Guid.Parse(ownerId)))
                Assert.That(storedOrganizationId, Is.EqualTo(Guid.Parse(organizationId)))
                Assert.That(storedRepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
            | other -> Assert.Fail($"Expected repository scope, got {other}.")

            use principalClient = createClientWithUserId principalId
            let! allowedPermission = checkPermissionAsync principalClient ownerId organizationId repositoryId "repo" "RepositoryRead" ""

            match allowedPermission with
            | Allowed reason -> Assert.That(reason, Does.Contain("RepositoryRead"))
            | Denied reason -> Assert.Fail($"Expected repo read to be allowed after grant. {reason}")

            do! revokeRoleAsync Client ownerId organizationId repositoryId "repo" "RepositoryReader" principalId

            let! afterRevoke = listRoleAssignmentsAsync Client ownerId organizationId repositoryId "repo"

            let stillPresent =
                afterRevoke
                |> List.exists (fun assignment ->
                    assignment.Principal.PrincipalId = principalId
                    && assignment.RoleId = "RepositoryReader")

            Assert.That(stillPresent, Is.False)

            let! deniedPermission = checkPermissionAsync principalClient ownerId organizationId repositoryId "repo" "RepositoryRead" ""

            match deniedPermission with
            | Denied reason -> Assert.That(reason, Does.Contain("RepositoryRead"))
            | Allowed reason -> Assert.Fail($"Expected repo read to be denied after revoke. {reason}")
        }

    /// Verifies the path permission deny overrides allow in check permission scenario.
    [<Test>]
    member _.PathPermissionDenyOverridesAllowInCheckPermission() =
        task {
            let! organizationId = createOrganizationAsync Client
            let! repositoryId = createRepositoryAsync Client organizationId

            do! grantRoleAsync Client ownerId organizationId "" "org" "OrganizationAdmin" testUserId

            do!
                upsertPathPermissionAsync
                    Client
                    ownerId
                    organizationId
                    repositoryId
                    "/images"
                    [
                        ("engineering", "NoAccess")
                        ("writers", "Modify")
                    ]

            use claimsClient =
                createClientWithClaims [ "engineering"
                                         "writers" ]

            let! permission = checkPermissionAsync claimsClient ownerId organizationId repositoryId "path" "PathWrite" "/images"

            match permission with
            | Denied _ -> ()
            | Allowed reason -> Assert.Fail($"Expected path write to be denied. {reason}")

            do! upsertPathPermissionAsync Client ownerId organizationId repositoryId "/images" [ ("engineering", "Modify") ]

            let! allowedPermission = checkPermissionAsync claimsClient ownerId organizationId repositoryId "path" "PathWrite" "/images"

            match allowedPermission with
            | Allowed _ -> ()
            | Denied reason -> Assert.Fail($"Expected path write to be allowed. {reason}")
        }

    /// Verifies the repository endpoint requires role assignment scenario.
    [<Test>]
    member _.RepositoryEndpointRequiresRoleAssignment() =
        task {
            let! orgId = createOrganizationAsync Client
            let! repoId = createRepositoryAsync Client orgId

            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- orgId
            parameters.RepositoryId <- repoId
            parameters.Description <- "Repo description update"
            parameters.CorrelationId <- generateCorrelationId ()

            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            let! deniedResponse = nonAdminClient.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            do! grantRoleAsync Client ownerId orgId "" "org" "OrganizationAdmin" nonAdminUserId

            let! allowedResponse = nonAdminClient.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(allowedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the storage upload metadata respects path permissions scenario.
    [<Test>]
    member _.StorageUploadMetadataRespectsPathPermissions() =
        task {
            let! orgId = createOrganizationAsync Client
            let! repoId = createRepositoryAsync Client orgId

            do! grantRoleAsync Client ownerId orgId "" "org" "OrganizationAdmin" testUserId

            do!
                upsertPathPermissionAsync
                    Client
                    ownerId
                    orgId
                    repoId
                    "images/foo.png"
                    [
                        ("engineering", "NoAccess")
                        ("writers", "Modify")
                    ]

            use claimsClient =
                createClientWithClaims [ "engineering"
                                         "writers" ]

            let fileVersion = AccessStorageTestData.createFileVersion "images/foo.png"

            let uploadParameters = Parameters.Storage.GetUploadMetadataForFilesParameters()
            uploadParameters.OwnerId <- ownerId
            uploadParameters.OrganizationId <- orgId
            uploadParameters.RepositoryId <- repoId
            uploadParameters.FileVersions <- [| fileVersion |]
            uploadParameters.CorrelationId <- generateCorrelationId ()

            let! deniedResponse = claimsClient.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent uploadParameters)
            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            do! upsertPathPermissionAsync Client ownerId orgId repoId "images/foo.png" [ ("engineering", "Modify") ]

            let! allowedResponse = claimsClient.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent uploadParameters)
            Assert.That(allowedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the branch annotate respects path read deny scenario.
    [<Test>]
    member _.BranchAnnotateRespectsPathReadDeny() =
        task {
            let repositoryId = repositoryIds[0]

            let branchParameters = Parameters.Branch.GetBranchParameters()
            branchParameters.OwnerId <- ownerId
            branchParameters.OrganizationId <- organizationId
            branchParameters.RepositoryId <- repositoryId
            branchParameters.BranchId <- repositoryDefaultBranchIds[0]
            branchParameters.CorrelationId <- generateCorrelationId ()

            let! branchResponse = Client.PostAsync("/branch/get", createJsonContent branchParameters)
            let! branchBody = branchResponse.Content.ReadAsStringAsync()
            Assert.That(branchResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), branchBody)

            let branch =
                (deserialize<GraceReturnValue<Grace.Types.Branch.BranchDto>> branchBody)
                    .ReturnValue

            do! grantRoleAsync Client ownerId organizationId repositoryId "repo" "RepositoryReader" testUserId

            do! upsertPathPermissionAsync Client ownerId organizationId repositoryId "sensitive/secret.txt" [ ("engineering", "NoAccess") ]

            use deniedClient = createClientWithClaims [ "engineering" ]

            let parameters = Parameters.Branch.AnnotateParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.BranchName <- $"{branch.BranchName}"
            parameters.TargetReferenceId <- branch.BasedOn.ReferenceId
            parameters.Path <- "sensitive/secret.txt"
            parameters.StartLine <- 1
            parameters.EndLine <- 1
            parameters.IncludeLineText <- true
            parameters.CorrelationId <- generateCorrelationId ()

            let! deniedResponse = deniedClient.PostAsync("/branch/annotate", createJsonContent parameters)
            let! deniedBody = deniedResponse.Content.ReadAsStringAsync()
            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), deniedBody)
        }

    /// Verifies the access grant role requires admin scope scenario.
    [<Test>]
    member _.AccessGrantRoleRequiresAdminScope() =
        task {
            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.ScopeKind <- "system"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- nonAdminUserId
            parameters.RoleId <- "SystemAdmin"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/authorize/grant-role", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the access grant role validates role ID scenario.
    [<Test>]
    member _.AccessGrantRoleValidatesRoleId() =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.ScopeKind <- "system"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- testUserId
            parameters.RoleId <- "NotARealRole"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/grant-role", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the access grant role validates scope applicability scenario.
    [<Test>]
    member _.AccessGrantRoleValidatesScopeApplicability() =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.ScopeKind <- "owner"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- testUserId
            parameters.RoleId <- "RepositoryAdmin"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/grant-role", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the access grant role rejects conflicting requested scope kind scenario.
    [<Test>]
    member _.AccessGrantRoleRejectsConflictingRequestedScopeKind() =
        task {
            let! organizationId = createOrganizationAsync Client
            let! repositoryId = createRepositoryAsync Client organizationId

            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ScopeKind <- "owner"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- $"{Guid.NewGuid()}"
            parameters.RoleId <- "RepositoryReader"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/grant-role", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the access revoke role rejects conflicting requested scope kind scenario.
    [<Test>]
    member _.AccessRevokeRoleRejectsConflictingRequestedScopeKind() =
        task {
            let! organizationId = createOrganizationAsync Client
            let! repositoryId = createRepositoryAsync Client organizationId

            let parameters = Parameters.Access.RevokeRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ScopeKind <- "owner"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- $"{Guid.NewGuid()}"
            parameters.RoleId <- "RepositoryReader"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/revoke-role", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the bootstrap seeds system admin scenario.
    [<Test>]
    member _.BootstrapSeedsSystemAdmin() =
        task {
            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.ScopeKind <- "system"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/list-role-assignments", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<GraceReturnValue<RoleAssignment list>> response

            let seeded =
                returnValue.ReturnValue
                |> List.exists (fun assignment ->
                    assignment.RoleId.Equals("SystemAdmin", StringComparison.OrdinalIgnoreCase)
                    && assignment.Principal.PrincipalId = testUserId
                    && assignment.Source = "bootstrap")

            Assert.That(seeded, Is.True)
        }

    /// Verifies the access show role assignments without owner shows only current principal system assignments scenario.
    [<Test>]
    member _.AccessShowRoleAssignmentsWithoutOwnerShowsOnlyCurrentPrincipalSystemAssignments() =
        task {
            let currentUserId = $"{Guid.NewGuid()}"
            let otherUserId = $"{Guid.NewGuid()}"

            do! grantRoleAsync Client "" "" "" "system" "SystemReader" currentUserId
            do! grantRoleAsync Client "" "" "" "system" "SystemOperator" otherUserId

            use currentClient = createClientWithUserId currentUserId
            let parameters = Parameters.Access.ShowRoleAssignmentsParameters()
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = currentClient.PostAsync("/authorize/show", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)

            let returnValue = deserialize<GraceReturnValue<RoleAssignment list>> responseText
            let assignments = returnValue.ReturnValue

            Assert.That(
                assignments
                |> List.exists (fun assignment ->
                    assignment.Principal.PrincipalId = currentUserId
                    && assignment.RoleId = "SystemReader"
                    && assignment.Scope = Scope.System),
                Is.True
            )

            Assert.That(
                assignments
                |> List.exists (fun assignment -> assignment.Principal.PrincipalId = otherUserId),
                Is.False
            )
        }

    /// Verifies the access show role assignments rejects repository without organization scenario.
    [<Test>]
    member _.AccessShowRoleAssignmentsRejectsRepositoryWithoutOrganization() =
        task {
            let parameters = Parameters.Access.ShowRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.RepositoryId <- $"{Guid.NewGuid()}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/show", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the access show role assignments rejects branch without repository scenario.
    [<Test>]
    member _.AccessShowRoleAssignmentsRejectsBranchWithoutRepository() =
        task {
            let! organizationId = createOrganizationAsync Client

            let parameters = Parameters.Access.ShowRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.BranchId <- $"{Guid.NewGuid()}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/show", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the access show role assignments rejects child scope without owner scenario.
    [<Test>]
    member _.AccessShowRoleAssignmentsRejectsChildScopeWithoutOwner() =
        task {
            let parameters = Parameters.Access.ShowRoleAssignmentsParameters()
            parameters.RepositoryId <- $"{Guid.NewGuid()}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/show", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the access list role assignments requires admin scope scenario.
    [<Test>]
    member _.AccessListRoleAssignmentsRequiresAdminScope() =
        task {
            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.ScopeKind <- "owner"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/authorize/list-role-assignments", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the access check permission restricts other principals scenario.
    [<Test>]
    member _.AccessCheckPermissionRestrictsOtherPrincipals() =
        task {
            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            let parameters = Parameters.Access.CheckPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[0]
            parameters.ResourceKind <- "repo"
            parameters.Operation <- "RepositoryRead"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- "other-user"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/authorize/check-permission", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    /// Verifies the access check permission allows self principal scenario.
    [<Test>]
    member _.AccessCheckPermissionAllowsSelfPrincipal() =
        task {
            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            let parameters = Parameters.Access.CheckPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[0]
            parameters.ResourceKind <- "repo"
            parameters.Operation <- "RepositoryRead"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- nonAdminUserId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/authorize/check-permission", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }


namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Branch
open Grace.Types.CacheRegistration
open Grace.Types.Common
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Text

/// Covers endpoint authorization scenarios.
[<Parallelizable(ParallelScope.All)>]
type EndpointAuthorizationTests() =

    /// Builds a deterministic client with user ID for integration setup fixture for the server integration access assertions.
    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Builds a deterministic unauthenticated client for integration setup fixture for the server integration access assertions.
    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

    /// Builds a syntactically complete but invalid Cache proof so runtime routes must reach their proof-handling boundary.
    let invalidCacheProof cacheId operation requestDigest =
        CacheRequestProofPayload.Create(cacheId, operation, requestDigest, getCurrentInstant ())
        |> fun payload -> SignedCacheRequestProof.Create(payload, "invalid-signature")

    /// Builds a valid serialized refresh body whose health field is replaced with an undefined numeric value for route-boundary validation.
    let numericHealthRefreshContent request health =
        task {
            use typedContent = createJsonContent request
            let! typedBody = typedContent.ReadAsStringAsync()

            let numericBody = System.Text.RegularExpressions.Regex.Replace(typedBody, "\\\"Health\\\"\\s*:\\s*\\\"[^\\\"]+\\\"", $"\"Health\":{health}")

            if typedBody = numericBody then
                invalidOp "Typed Cache refresh JSON did not include a Health field to replace."

            return new StringContent(numericBody, Encoding.UTF8, "application/json")
        }

    /// Grants role needed by authorization-sensitive tests.
    let grantRoleAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId principalId roleId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/grant-role", createJsonContent parameters)
        }

    /// Gets default branch from the running test server.
    let getDefaultBranchAsync (repositoryId: string) =
        task {
            let repositoryIndex =
                repositoryIds
                |> Array.findIndex (fun candidate -> candidate = repositoryId)

            let branchId = repositoryDefaultBranchIds[repositoryIndex]
            let parameters = Parameters.Branch.GetBranchParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            let timeoutAt = DateTime.UtcNow.AddSeconds(10.0)
            let mutable branch = None
            let mutable lastStatusCode = HttpStatusCode.OK
            let mutable lastResponseBody = String.Empty

            while branch.IsNone && DateTime.UtcNow < timeoutAt do
                parameters.CorrelationId <- generateCorrelationId ()

                let! response = Client.PostAsync("/branch/get", createJsonContent parameters)
                lastStatusCode <- response.StatusCode

                let! responseBody = response.Content.ReadAsStringAsync()
                lastResponseBody <- responseBody

                if response.IsSuccessStatusCode then
                    let returnValue = deserialize<GraceReturnValue<BranchDto>> responseBody
                    let branchDto = returnValue.ReturnValue

                    if branchDto.BranchId <> Guid.Empty
                       && branchDto.LatestPromotion.ReferenceId
                          <> Guid.Empty then
                        branch <- Some branchDto

                if branch.IsNone then
                    do! System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(250.0))

            match branch with
            | Some branch -> return branch
            | None ->
                Assert.Fail(
                    $"Timed out waiting for repository {repositoryId} to expose initial branch {branchId} through /branch/get. Last status: {lastStatusCode}; body: {lastResponseBody}"
                )

                return Unchecked.defaultof<BranchDto>
        }

    /// Builds create owner get parameters for route calls.
    let createOwnerGetParameters () =
        let parameters = Parameters.Owner.GetOwnerParameters()
        parameters.OwnerId <- ownerId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create owner set name parameters for route calls.
    let createOwnerSetNameParameters () =
        let parameters = Parameters.Owner.SetOwnerNameParameters()
        parameters.OwnerId <- ownerId
        parameters.NewName <- $"Owner{Guid.NewGuid():N}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create organization get parameters for route calls.
    let createOrganizationGetParameters () =
        let parameters = Parameters.Organization.GetOrganizationParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create organization set name parameters for route calls.
    let createOrganizationSetNameParameters () =
        let parameters = Parameters.Organization.SetOrganizationNameParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.NewName <- $"Org{Guid.NewGuid():N}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create repository get parameters for route calls.
    let createRepositoryGetParameters (repositoryId: string) =
        let parameters = Parameters.Repository.GetRepositoryParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create repository set visibility parameters for route calls.
    let createRepositorySetVisibilityParameters (repositoryId: string) =
        let parameters = Parameters.Repository.SetRepositoryVisibilityParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Visibility <- "Public"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create work item parameters for route calls.
    let createWorkItemParameters (repositoryId: string) =
        let parameters = Parameters.WorkItem.CreateWorkItemParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.WorkItemId <- $"{Guid.NewGuid()}"
        parameters.Title <- $"Work{Guid.NewGuid():N}"
        parameters.Description <- "Test work item"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create branch get parameters for route calls.
    let createBranchGetParameters (branch: BranchDto) =
        let parameters = Parameters.Branch.GetBranchParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{branch.RepositoryId}"
        parameters.BranchId <- $"{branch.BranchId}"
        parameters.BranchName <- $"{branch.BranchName}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create branch annotate parameters for route calls.
    let createBranchAnnotateParameters (branch: BranchDto) =
        let parameters = Parameters.Branch.AnnotateParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{branch.RepositoryId}"
        parameters.BranchId <- $"{branch.BranchId}"
        parameters.BranchName <- $"{branch.BranchName}"
        parameters.TargetReferenceId <- branch.BasedOn.ReferenceId
        parameters.Path <- "does-not-matter-for-auth.txt"
        parameters.StartLine <- 1
        parameters.EndLine <- 1
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create branch commit parameters for route calls.
    let createBranchCommitParameters (branch: BranchDto) =
        let parameters = Parameters.Branch.CreateReferenceParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{branch.RepositoryId}"
        parameters.BranchId <- $"{branch.BranchId}"
        parameters.BranchName <- $"{branch.BranchName}"
        parameters.DirectoryVersionId <- branch.LatestPromotion.DirectoryId
        parameters.Sha256Hash <- $"{branch.LatestPromotion.Sha256Hash}"
        parameters.Message <- "Commit from test"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create branch enable commit parameters for route calls.
    let createBranchEnableCommitParameters (branch: BranchDto) =
        let parameters = Parameters.Branch.EnableFeatureParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{branch.RepositoryId}"
        parameters.BranchId <- $"{branch.BranchId}"
        parameters.BranchName <- $"{branch.BranchName}"
        parameters.Enabled <- true
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create download parameters for route calls.
    let createDownloadParameters (repositoryId: string) =
        let parameters = Parameters.Storage.GetDownloadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersion <- AccessStorageTestData.createFileVersion "images/test.txt"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds create upload parameters for route calls.
    let createUploadParameters (repositoryId: string) =
        let parameters = Parameters.Storage.GetUploadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId

        parameters.FileVersions <-
            [|
                AccessStorageTestData.createFileVersion "images/test.txt"
            |]

        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Verifies the allow anonymous endpoints return success scenario.
    [<Test>]
    member _.AllowAnonymousEndpointsReturnSuccess() =
        task {
            use client = createUnauthenticatedClient ()

            let! rootResponse = client.GetAsync("/")
            Assert.That(rootResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! healthResponse = client.GetAsync("/healthz")
            Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! configResponse = client.GetAsync("/authenticate/oidc/config")
            Assert.That(configResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! loginResponse = client.GetAsync("/authenticate/login")
            Assert.That(loginResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Redirect))

            let! providerLoginResponse = client.GetAsync("/authenticate/login/test")
            Assert.That(providerLoginResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Redirect, HttpStatusCode.NotFound))
        }

    /// Verifies proof-only Cache runtime routes reach validation without a Grace user while administrative routes retain fallback authentication.
    [<Test>]
    member _.CacheProofRoutesReachValidationWithoutGraceUserAndAdministrativeRoutesRemainProtected() =
        task {
            let cacheId = Guid.NewGuid()
            use unauthenticatedClient = createUnauthenticatedClient ()

            let refreshRequest =
                {
                    Class = nameof CacheRegistrationRefreshRequest
                    CacheId = cacheId
                    Endpoint = "https://cache.example.test"
                    Health = CacheHealthStatus.Healthy
                    SoftwareVersion = "1.0.0"
                    ProtocolVersion = "1"
                    PrefetchSupported = false
                    ObservedAt = getCurrentInstant ()
                    Proof = invalidCacheProof cacheId CacheRegistrationProof.RefreshOperation "invalid-refresh-digest"
                }

            let! refreshResponse = unauthenticatedClient.PostAsync("/cache/refresh", createJsonContent refreshRequest)
            Assert.That(refreshResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unhealthyRefreshResponse =
                unauthenticatedClient.PostAsync("/cache/refresh", createJsonContent { refreshRequest with Health = CacheHealthStatus.Unhealthy })

            Assert.That(unhealthyRefreshResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            for unsupportedHealth in [ 0; 999 ] do
                let! unsupportedHealthContent = numericHealthRefreshContent refreshRequest unsupportedHealth
                let! unsupportedHealthResponse = unauthenticatedClient.PostAsync("/cache/refresh", unsupportedHealthContent)

                let! unsupportedHealthBody = unsupportedHealthResponse.Content.ReadAsStringAsync()
                Assert.That(unsupportedHealthResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                Assert.That(unsupportedHealthBody, Does.Contain "Health must be Healthy or Unhealthy.")

            let invalidKey = { Class = nameof CacheIdentityPublicKey; Algorithm = "ES256"; Curve = "P-256"; PublicKeyX = "invalid"; PublicKeyY = "invalid" }

            let rotationRequest =
                {
                    Class = nameof CacheKeyRotationRequest
                    CacheId = cacheId
                    NewPublicKey = invalidKey
                    Proof = invalidCacheProof cacheId CacheRegistrationProof.RotateKeyOperation "invalid-rotation-digest"
                }

            let! rotationResponse = unauthenticatedClient.PostAsync("/cache/rotate-key", createJsonContent rotationRequest)
            Assert.That(rotationResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let missingKeyRotationRequest = { rotationRequest with NewPublicKey = Unchecked.defaultof<CacheIdentityPublicKey> }

            let! missingKeyRotationResponse = unauthenticatedClient.PostAsync("/cache/rotate-key", createJsonContent missingKeyRotationRequest)

            Assert.That(missingKeyRotationResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let! enrollmentResponse = unauthenticatedClient.PostAsync("/cache/enroll", createJsonContent Unchecked.defaultof<CacheEnrollmentRequest>)
            Assert.That(enrollmentResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! assignmentResponse =
                unauthenticatedClient.PostAsync("/cache/assign-repositories", createJsonContent Unchecked.defaultof<CacheRepositoryAssignmentRequest>)

            Assert.That(assignmentResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! revocationResponse = unauthenticatedClient.PostAsync("/cache/revoke", createJsonContent Unchecked.defaultof<CacheRevocationRequest>)
            Assert.That(revocationResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    /// Verifies the metrics endpoint requires system admin scenario.
    [<Test>]
    member _.MetricsEndpointRequiresSystemAdmin() =
        task {
            use unauthClient = createUnauthenticatedClient ()
            let! unauthResponse = unauthClient.GetAsync("/metrics")
            Assert.That(unauthResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let nonAdminUser = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUser
            let! nonAdminResponse = nonAdminClient.GetAsync("/metrics")
            Assert.That(nonAdminResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! adminResponse = Client.GetAsync("/metrics")
            Assert.That(adminResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the owner endpoints require authorization scenario.
    [<Test>]
    member _.OwnerEndpointsRequireAuthorization() =
        task {
            let ownerReader = $"{Guid.NewGuid()}"
            let ownerAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "owner" ownerId "" "" "" ownerReader "OwnerReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "owner" ownerId "" "" "" ownerAdmin "OwnerAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId ownerReader
            use adminClient = createClientWithUserId ownerAdmin
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let! unauthGet = unauthClient.PostAsync("/owner/get", createJsonContent (createOwnerGetParameters ()))
            Assert.That(unauthGet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedGet = unprivilegedClient.PostAsync("/owner/get", createJsonContent (createOwnerGetParameters ()))
            Assert.That(deniedGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedGet = readerClient.PostAsync("/owner/get", createJsonContent (createOwnerGetParameters ()))
            Assert.That(allowedGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unauthSet = unauthClient.PostAsync("/owner/setName", createJsonContent (createOwnerSetNameParameters ()))

            Assert.That(unauthSet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedSet = readerClient.PostAsync("/owner/setName", createJsonContent (createOwnerSetNameParameters ()))

            Assert.That(deniedSet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedSet = adminClient.PostAsync("/owner/setName", createJsonContent (createOwnerSetNameParameters ()))

            Assert.That(allowedSet.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the organization endpoints require authorization scenario.
    [<Test>]
    member _.OrganizationEndpointsRequireAuthorization() =
        task {
            let orgReader = $"{Guid.NewGuid()}"
            let orgAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "org" ownerId organizationId "" "" orgReader "OrganizationReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "org" ownerId organizationId "" "" orgAdmin "OrganizationAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId orgReader
            use adminClient = createClientWithUserId orgAdmin
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let! unauthGet = unauthClient.PostAsync("/organization/get", createJsonContent (createOrganizationGetParameters ()))
            Assert.That(unauthGet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedGet = unprivilegedClient.PostAsync("/organization/get", createJsonContent (createOrganizationGetParameters ()))
            Assert.That(deniedGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedGet = readerClient.PostAsync("/organization/get", createJsonContent (createOrganizationGetParameters ()))
            Assert.That(allowedGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unauthSet = unauthClient.PostAsync("/organization/setName", createJsonContent (createOrganizationSetNameParameters ()))

            Assert.That(unauthSet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedSet = readerClient.PostAsync("/organization/setName", createJsonContent (createOrganizationSetNameParameters ()))

            Assert.That(deniedSet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedSet = adminClient.PostAsync("/organization/setName", createJsonContent (createOrganizationSetNameParameters ()))

            Assert.That(allowedSet.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the repository endpoints require authorization scenario.
    [<Test>]
    member _.RepositoryEndpointsRequireAuthorization() =
        task {
            let repositoryId = repositoryIds[0]
            let repoReader = $"{Guid.NewGuid()}"
            let repoAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoAdmin "RepositoryAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId repoReader
            use adminClient = createClientWithUserId repoAdmin
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let! unauthGet = unauthClient.PostAsync("/repository/get", createJsonContent (createRepositoryGetParameters repositoryId))
            Assert.That(unauthGet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedGet = unprivilegedClient.PostAsync("/repository/get", createJsonContent (createRepositoryGetParameters repositoryId))
            Assert.That(deniedGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedGet = readerClient.PostAsync("/repository/get", createJsonContent (createRepositoryGetParameters repositoryId))
            Assert.That(allowedGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unauthSet = unauthClient.PostAsync("/repository/setVisibility", createJsonContent (createRepositorySetVisibilityParameters repositoryId))

            Assert.That(unauthSet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedSet = readerClient.PostAsync("/repository/setVisibility", createJsonContent (createRepositorySetVisibilityParameters repositoryId))

            Assert.That(deniedSet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedSet = adminClient.PostAsync("/repository/setVisibility", createJsonContent (createRepositorySetVisibilityParameters repositoryId))

            Assert.That(allowedSet.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the work create requires repository write scenario.
    [<Test>]
    member _.WorkCreateRequiresRepositoryWrite() =
        task {
            let repositoryId = repositoryIds[0]
            let repoReader = $"{Guid.NewGuid()}"
            let repoWriter = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoWriter "RepositoryContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId repoReader
            use writerClient = createClientWithUserId repoWriter

            let! unauthResponse = unauthClient.PostAsync("/work/create", createJsonContent (createWorkItemParameters repositoryId))

            Assert.That(unauthResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedResponse = readerClient.PostAsync("/work/create", createJsonContent (createWorkItemParameters repositoryId))

            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedResponse = writerClient.PostAsync("/work/create", createJsonContent (createWorkItemParameters repositoryId))

            Assert.That(allowedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the branch endpoints require authorization scenario.
    [<Test>]
    member _.BranchEndpointsRequireAuthorization() =
        task {
            let repositoryId = repositoryIds[0]
            let! branch = getDefaultBranchAsync repositoryId

            let branchReader = $"{Guid.NewGuid()}"
            let branchWriter = $"{Guid.NewGuid()}"
            let branchAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" branchReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" branchWriter "RepositoryContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" branchAdmin "RepositoryAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId branchReader
            use writerClient = createClientWithUserId branchWriter
            use adminClient = createClientWithUserId branchAdmin
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let! unauthGet = unauthClient.PostAsync("/branch/get", createJsonContent (createBranchGetParameters branch))
            Assert.That(unauthGet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedGet = unprivilegedClient.PostAsync("/branch/get", createJsonContent (createBranchGetParameters branch))
            Assert.That(deniedGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedGet = readerClient.PostAsync("/branch/get", createJsonContent (createBranchGetParameters branch))
            Assert.That(allowedGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unauthAnnotate = unauthClient.PostAsync("/branch/annotate", createJsonContent (createBranchAnnotateParameters branch))
            Assert.That(unauthAnnotate.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedAnnotate = unprivilegedClient.PostAsync("/branch/annotate", createJsonContent (createBranchAnnotateParameters branch))
            Assert.That(deniedAnnotate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerAnnotate = readerClient.PostAsync("/branch/annotate", createJsonContent (createBranchAnnotateParameters branch))
            Assert.That(readerAnnotate.StatusCode, Is.Not.EqualTo(HttpStatusCode.Unauthorized))
            Assert.That(readerAnnotate.StatusCode, Is.Not.EqualTo(HttpStatusCode.Forbidden))

            let! enableCommitResponse = adminClient.PostAsync("/branch/enableCommit", createJsonContent (createBranchEnableCommitParameters branch))

            Assert.That(enableCommitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! deniedEnable = writerClient.PostAsync("/branch/enableCommit", createJsonContent (createBranchEnableCommitParameters branch))

            Assert.That(deniedEnable.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! unauthCommit = unauthClient.PostAsync("/branch/commit", createJsonContent (createBranchCommitParameters branch))

            Assert.That(unauthCommit.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedCommit = unprivilegedClient.PostAsync("/branch/commit", createJsonContent (createBranchCommitParameters branch))

            Assert.That(deniedCommit.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedCommit = writerClient.PostAsync("/branch/commit", createJsonContent (createBranchCommitParameters branch))

            Assert.That(allowedCommit.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the storage endpoints require path authorization scenario.
    [<Test>]
    member _.StorageEndpointsRequirePathAuthorization() =
        task {
            let repositoryId = repositoryIds[0]
            let pathReader = $"{Guid.NewGuid()}"
            let pathWriter = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathWriter "RepositoryContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId pathReader
            use writerClient = createClientWithUserId pathWriter
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let! unauthDownload = unauthClient.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId))

            Assert.That(unauthDownload.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedDownload = unprivilegedClient.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId))

            Assert.That(deniedDownload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedDownload = readerClient.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId))

            Assert.That(allowedDownload.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unauthUpload = unauthClient.PostAsync("/storage/getUploadUri", createJsonContent (createUploadParameters repositoryId))

            Assert.That(unauthUpload.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedUpload = unprivilegedClient.PostAsync("/storage/getUploadUri", createJsonContent (createUploadParameters repositoryId))

            Assert.That(deniedUpload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedUpload = writerClient.PostAsync("/storage/getUploadUri", createJsonContent (createUploadParameters repositoryId))

            Assert.That(allowedUpload.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }
