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
                    "/images/foo.png"
                    [
                        ("engineering", "NoAccess")
                        ("writers", "Modify")
                    ]

            use claimsClient =
                createClientWithClaims [ "engineering"
                                         "writers" ]

            let fileVersion = FileVersion.Create "/images/foo.png" "hash" "" false 1L

            let uploadParameters = Parameters.Storage.GetUploadMetadataForFilesParameters()
            uploadParameters.OwnerId <- ownerId
            uploadParameters.OrganizationId <- orgId
            uploadParameters.RepositoryId <- repoId
            uploadParameters.FileVersions <- [| fileVersion |]
            uploadParameters.CorrelationId <- generateCorrelationId ()

            let! deniedResponse = claimsClient.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent uploadParameters)
            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            do! upsertPathPermissionAsync Client ownerId orgId repoId "/images/foo.png" [ ("engineering", "Modify") ]

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
