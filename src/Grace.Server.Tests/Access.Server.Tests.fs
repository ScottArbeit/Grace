namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http

[<NonParallelizable>]
type AccessBootstrapEnabledTests() =

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

    let createGrantRoleParameters (roleId: string) (principalId: string) =
        let parameters = Parameters.Access.GrantRoleParameters()
        parameters.ScopeKind <- "system"
        parameters.PrincipalType <- "User"
        parameters.PrincipalId <- principalId
        parameters.RoleId <- roleId
        parameters.Source <- "test"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    [<Test>]
    member _.BootstrapSeedsSystemAdminWhenConfigured() =
        task {
            use client = createClient testUserId
            let! _ownerId = createOwner client

            let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/access/grantRole", createJsonContent parameters)

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

[<NonParallelizable>]
type AccessBootstrapTests() =

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

    let createGrantRoleParameters (roleId: string) (principalId: string) =
        let parameters = Parameters.Access.GrantRoleParameters()
        parameters.ScopeKind <- "system"
        parameters.PrincipalType <- "User"
        parameters.PrincipalId <- principalId
        parameters.RoleId <- roleId
        parameters.Source <- "test"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    [<Test>]
    member _.BootstrapDisabledByDefaultReturnsForbidden() =
        task {
            use client = createClient $"{Guid.NewGuid()}"
            let! _ownerId = createOwner client

            let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/access/grantRole", createJsonContent parameters)

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

[<Parallelizable(ParallelScope.All)>]
type AccessCheckPermissionTests() =

    let createClient (userId: string) (groups: string list) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)

        if not groups.IsEmpty then
            client.DefaultRequestHeaders.Add("x-grace-groups", String.Join(";", groups))

        client

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

            return! client.PostAsync("/access/checkPermission", createJsonContent parameters)
        }

    [<Test>]
    member _.SelfCheckWithoutPrincipalSpecifiedReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "" ""
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.ExplicitSelfPrincipalReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "User" userId

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.GroupPrincipalMemberReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            let groupId = $"{Guid.NewGuid()}"
            use client = createClient userId [ groupId ]

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "Group" groupId

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.OtherPrincipalRequiresAdmin() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "User" $"{Guid.NewGuid()}"

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

[<Parallelizable(ParallelScope.All)>]
type AccessControlSecurityTests() =

    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

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

            return! client.PostAsync("/access/grantRole", createJsonContent parameters)
        }

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

            return! client.PostAsync("/access/revokeRole", createJsonContent parameters)
        }

    let listRoleAssignmentsAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId =
        task {
            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.ScopeKind <- scopeKind
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/access/listRoleAssignments", createJsonContent parameters)
        }

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

    [<Test>]
    member _.OrganizationScopeBoundariesEnforced() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgA = $"{Guid.NewGuid()}"
            let orgB = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "org" ownerId orgA "" "" adminUser "OrgAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser

            let! allowed = grantRoleAsync adminClient "org" ownerId orgA "" "" $"{Guid.NewGuid()}" "OrgReader"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync adminClient "org" ownerId orgB "" "" $"{Guid.NewGuid()}" "OrgReader"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! revokeForbidden = revokeRoleAsync adminClient "org" ownerId orgB "" "" $"{Guid.NewGuid()}" "OrgReader"
            Assert.That(revokeForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! listAllowed = listRoleAssignmentsAsync adminClient "org" ownerId orgA "" ""
            Assert.That(listAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listForbidden = listRoleAssignmentsAsync adminClient "org" ownerId orgB "" ""
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.RepositoryScopeBoundariesEnforced() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoA = $"{Guid.NewGuid()}"
            let repoB = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = grantRoleAsync Client "repo" ownerId orgId repoA "" adminUser "RepoAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = createClientWithUserId adminUser

            let! allowed = grantRoleAsync adminClient "repo" ownerId orgId repoA "" $"{Guid.NewGuid()}" "RepoReader"
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! forbidden = grantRoleAsync adminClient "repo" ownerId orgId repoB "" $"{Guid.NewGuid()}" "RepoReader"
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! revokeForbidden = revokeRoleAsync adminClient "repo" ownerId orgId repoB "" $"{Guid.NewGuid()}" "RepoReader"
            Assert.That(revokeForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! listAllowed = listRoleAssignmentsAsync adminClient "repo" ownerId orgId repoA ""
            Assert.That(listAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listForbidden = listRoleAssignmentsAsync adminClient "repo" ownerId orgId repoB ""
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

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

    [<Test>]
    member _.PrivilegeEscalationPrevention() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoId = $"{Guid.NewGuid()}"

            let repoAdminUser = $"{Guid.NewGuid()}"
            let orgAdminUser = $"{Guid.NewGuid()}"
            let repoWriterUser = $"{Guid.NewGuid()}"

            let! grantRepoAdmin = grantRoleAsync Client "repo" ownerId orgId repoId "" repoAdminUser "RepoAdmin"
            Assert.That(grantRepoAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantOrgAdmin = grantRoleAsync Client "org" ownerId orgId "" "" orgAdminUser "OrgAdmin"
            Assert.That(grantOrgAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantRepoWrite = grantRoleAsync Client "repo" ownerId orgId repoId "" repoWriterUser "RepoContributor"
            Assert.That(grantRepoWrite.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use repoAdminClient = createClientWithUserId repoAdminUser
            use orgAdminClient = createClientWithUserId orgAdminUser
            use repoWriterClient = createClientWithUserId repoWriterUser

            let! repoAdminCannotGrantOrg = grantRoleAsync repoAdminClient "org" ownerId orgId "" "" $"{Guid.NewGuid()}" "OrgAdmin"

            Assert.That(repoAdminCannotGrantOrg.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! orgAdminCannotGrantOwner = grantRoleAsync orgAdminClient "owner" ownerId "" "" "" $"{Guid.NewGuid()}" "OwnerAdmin"

            Assert.That(orgAdminCannotGrantOwner.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! repoWriterCannotGrantRepoAdmin = grantRoleAsync repoWriterClient "repo" ownerId orgId repoId "" $"{Guid.NewGuid()}" "RepoAdmin"

            Assert.That(repoWriterCannotGrantRepoAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.GrantRoleRejectsInvalidRole() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoId = $"{Guid.NewGuid()}"

            let! response = grantRoleAsync Client "repo" ownerId orgId repoId "" $"{Guid.NewGuid()}" "NotARole"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.GrantRoleRejectsRoleScopeMismatch() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"

            let! response = grantRoleAsync Client "org" ownerId orgId "" "" $"{Guid.NewGuid()}" "RepoAdmin"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }


namespace Grace.Server.Tests

open FSharp.Control
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open NUnit.Framework
open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type AccessControl() =

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

            let! response = client.PostAsync("/access/grantRole", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

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

            let! response = client.PostAsync("/access/upsertPathPermission", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

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

            let! response = client.PostAsync("/access/checkPermission", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PermissionCheckResult>> response
            return returnValue.ReturnValue
        }

    let createClientWithClaims (claims: string list) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", testUserId)

        if not (List.isEmpty claims) then
            client.DefaultRequestHeaders.Add("x-grace-claims", String.Join(";", claims))

        client

    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

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

    [<Test>]
    member _.RoleInheritanceResolvesPermissionsAcrossOrganizations() =
        task {
            let! orgA = createOrganizationAsync Client
            let! orgB = createOrganizationAsync Client
            let! repoA = createRepositoryAsync Client orgA
            let! repoB = createRepositoryAsync Client orgB

            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            do! grantRoleAsync Client ownerId orgA "" "org" "OrgAdmin" nonAdminUserId
            do! grantRoleAsync Client ownerId orgB "" "org" "OrgReader" nonAdminUserId

            let! repoAWrite = checkPermissionAsync nonAdminClient ownerId orgA repoA "repo" "RepoWrite" ""
            let! repoBWrite = checkPermissionAsync nonAdminClient ownerId orgB repoB "repo" "RepoWrite" ""
            let! repoBRead = checkPermissionAsync nonAdminClient ownerId orgB repoB "repo" "RepoRead" ""

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

    [<Test>]
    member _.PathPermissionDenyOverridesAllowInCheckPermission() =
        task {
            let! organizationId = createOrganizationAsync Client
            let! repositoryId = createRepositoryAsync Client organizationId

            do! grantRoleAsync Client ownerId organizationId "" "org" "OrgAdmin" testUserId

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

            do! grantRoleAsync Client ownerId orgId "" "org" "OrgAdmin" nonAdminUserId

            let! allowedResponse = nonAdminClient.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(allowedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.StorageUploadMetadataRespectsPathPermissions() =
        task {
            let! orgId = createOrganizationAsync Client
            let! repoId = createRepositoryAsync Client orgId

            do! grantRoleAsync Client ownerId orgId "" "org" "OrgAdmin" testUserId

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

            let fileVersion = FileVersion.Create "images/foo.png" "hash" "" false 1L

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

            let! response = nonAdminClient.PostAsync("/access/grantRole", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.AccessGrantRoleValidatesRoleId() =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.ScopeKind <- "system"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- testUserId
            parameters.RoleId <- "NotARealRole"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/access/grantRole", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.AccessGrantRoleValidatesScopeApplicability() =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.ScopeKind <- "owner"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- testUserId
            parameters.RoleId <- "RepoAdmin"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/access/grantRole", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.BootstrapSeedsSystemAdmin() =
        task {
            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.ScopeKind <- "system"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/access/listRoleAssignments", createJsonContent parameters)
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

    [<Test>]
    member _.AccessListRoleAssignmentsRequiresAdminScope() =
        task {
            let nonAdminUserId = $"{Guid.NewGuid()}"
            use nonAdminClient = createClientWithUserId nonAdminUserId

            let parameters = Parameters.Access.ListRoleAssignmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.ScopeKind <- "owner"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/access/listRoleAssignments", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

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
            parameters.Operation <- "RepoRead"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- "other-user"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/access/checkPermission", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

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
            parameters.Operation <- "RepoRead"
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- nonAdminUserId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = nonAdminClient.PostAsync("/access/checkPermission", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }


namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Branch
open Grace.Types.Types
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json

[<Parallelizable(ParallelScope.All)>]
type EndpointAuthorizationTests() =

    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

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

            return! client.PostAsync("/access/grantRole", createJsonContent parameters)
        }

    let getDefaultBranchAsync (repositoryId: string) =
        task {
            let repositoryIndex = repositoryIds |> Array.findIndex (fun candidate -> candidate = repositoryId)
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

                    if branchDto.BranchId <> Guid.Empty && branchDto.LatestPromotion.ReferenceId <> Guid.Empty then
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

    let createOwnerGetParameters () =
        let parameters = Parameters.Owner.GetOwnerParameters()
        parameters.OwnerId <- ownerId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createOwnerSetNameParameters () =
        let parameters = Parameters.Owner.SetOwnerNameParameters()
        parameters.OwnerId <- ownerId
        parameters.NewName <- $"Owner{Guid.NewGuid():N}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createOrganizationGetParameters () =
        let parameters = Parameters.Organization.GetOrganizationParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createOrganizationSetNameParameters () =
        let parameters = Parameters.Organization.SetOrganizationNameParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.NewName <- $"Org{Guid.NewGuid():N}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createRepositoryGetParameters (repositoryId: string) =
        let parameters = Parameters.Repository.GetRepositoryParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createRepositorySetVisibilityParameters (repositoryId: string) =
        let parameters = Parameters.Repository.SetRepositoryVisibilityParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Visibility <- "Public"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

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

    let createBranchGetParameters (branch: BranchDto) =
        let parameters = Parameters.Branch.GetBranchParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{branch.RepositoryId}"
        parameters.BranchId <- $"{branch.BranchId}"
        parameters.BranchName <- $"{branch.BranchName}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

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

    let createDownloadParameters (repositoryId: string) =
        let parameters = Parameters.Storage.GetDownloadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersion <- FileVersion.Create "images/test.txt" "hash" "" false 1L
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createUploadParameters (repositoryId: string) =
        let parameters = Parameters.Storage.GetUploadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId

        parameters.FileVersions <-
            [|
                FileVersion.Create "images/test.txt" "hash" "" false 1L
            |]

        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    [<Test>]
    member _.AllowAnonymousEndpointsReturnSuccess() =
        task {
            use client = createUnauthenticatedClient ()

            let! rootResponse = client.GetAsync("/")
            Assert.That(rootResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! healthResponse = client.GetAsync("/healthz")
            Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! configResponse = client.GetAsync("/auth/oidc/config")
            Assert.That(configResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! loginResponse = client.GetAsync("/auth/login")
            Assert.That(loginResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Redirect))

            let! providerLoginResponse = client.GetAsync("/auth/login/test")
            Assert.That(providerLoginResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Redirect, HttpStatusCode.NotFound))
        }

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

    [<Test>]
    member _.OrganizationEndpointsRequireAuthorization() =
        task {
            let orgReader = $"{Guid.NewGuid()}"
            let orgAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "org" ownerId organizationId "" "" orgReader "OrgReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "org" ownerId organizationId "" "" orgAdmin "OrgAdmin"
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

    [<Test>]
    member _.RepositoryEndpointsRequireAuthorization() =
        task {
            let repositoryId = repositoryIds[0]
            let repoReader = $"{Guid.NewGuid()}"
            let repoAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepoReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoAdmin "RepoAdmin"
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

    [<Test>]
    member _.WorkCreateRequiresRepoWrite() =
        task {
            let repositoryId = repositoryIds[0]
            let repoReader = $"{Guid.NewGuid()}"
            let repoWriter = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepoReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoWriter "RepoContributor"
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

    [<Test>]
    member _.BranchEndpointsRequireAuthorization() =
        task {
            let repositoryId = repositoryIds[0]
            let! branch = getDefaultBranchAsync repositoryId

            let branchReader = $"{Guid.NewGuid()}"
            let branchWriter = $"{Guid.NewGuid()}"
            let branchAdmin = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" branchReader "RepoReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" branchWriter "RepoContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantAdmin = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" branchAdmin "RepoAdmin"
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

    [<Test>]
    member _.StorageEndpointsRequirePathAuthorization() =
        task {
            let repositoryId = repositoryIds[0]
            let pathReader = $"{Guid.NewGuid()}"
            let pathWriter = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathReader "RepoReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathWriter "RepoContributor"
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
