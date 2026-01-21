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

            let! repoAdminCannotGrantOrg =
                grantRoleAsync repoAdminClient "org" ownerId orgId "" "" $"{Guid.NewGuid()}" "OrgAdmin"

            Assert.That(repoAdminCannotGrantOrg.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! orgAdminCannotGrantOwner =
                grantRoleAsync orgAdminClient "owner" ownerId "" "" "" $"{Guid.NewGuid()}" "OwnerAdmin"

            Assert.That(orgAdminCannotGrantOwner.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! repoWriterCannotGrantRepoAdmin =
                grantRoleAsync repoWriterClient "repo" ownerId orgId repoId "" $"{Guid.NewGuid()}" "RepoAdmin"

            Assert.That(repoWriterCannotGrantRepoAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.GrantRoleRejectsInvalidRole() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"
            let repoId = $"{Guid.NewGuid()}"

            let! response =
                grantRoleAsync Client "repo" ownerId orgId repoId "" $"{Guid.NewGuid()}" "NotARole"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.GrantRoleRejectsRoleScopeMismatch() =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let orgId = $"{Guid.NewGuid()}"

            let! response =
                grantRoleAsync Client "org" ownerId orgId "" "" $"{Guid.NewGuid()}" "RepoAdmin"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }
