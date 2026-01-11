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

    let grantRoleAsync (client: HttpClient) ownerId organizationId repositoryId scopeKind roleId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- testUserId
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

            do! grantRoleAsync Client ownerId orgA "" "org" "OrgAdmin"
            do! grantRoleAsync Client ownerId orgB "" "org" "OrgReader"

            let! repoAWrite = checkPermissionAsync Client ownerId orgA repoA "repo" "RepoWrite" ""
            let! repoBWrite = checkPermissionAsync Client ownerId orgB repoB "repo" "RepoWrite" ""
            let! repoBRead = checkPermissionAsync Client ownerId orgB repoB "repo" "RepoRead" ""

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

            do! grantRoleAsync Client ownerId organizationId "" "org" "OrgAdmin"

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

            let! deniedResponse = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            do! grantRoleAsync Client ownerId orgId "" "org" "OrgAdmin"

            let! allowedResponse = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(allowedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.StorageUploadMetadataRespectsPathPermissions() =
        task {
            let! orgId = createOrganizationAsync Client
            let! repoId = createRepositoryAsync Client orgId

            do! grantRoleAsync Client ownerId orgId "" "org" "OrgAdmin"

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
