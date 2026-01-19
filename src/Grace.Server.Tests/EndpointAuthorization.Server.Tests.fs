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
            let parameters = Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/getBranches", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<GraceReturnValue<BranchDto array>> response
            return returnValue.ReturnValue |> Array.head
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
        parameters.FileVersion <- FileVersion.Create "/images/test.txt" "hash" "" false 1L
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createUploadParameters (repositoryId: string) =
        let parameters = Parameters.Storage.GetUploadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersions <- [| FileVersion.Create "/images/test.txt" "hash" "" false 1L |]
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
            Assert.That(providerLoginResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Redirect))
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

            let! unauthSet =
                unauthClient.PostAsync("/owner/setName", createJsonContent (createOwnerSetNameParameters ()))

            Assert.That(unauthSet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedSet =
                readerClient.PostAsync("/owner/setName", createJsonContent (createOwnerSetNameParameters ()))

            Assert.That(deniedSet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedSet =
                adminClient.PostAsync("/owner/setName", createJsonContent (createOwnerSetNameParameters ()))

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

            let! unauthSet =
                unauthClient.PostAsync("/organization/setName", createJsonContent (createOrganizationSetNameParameters ()))

            Assert.That(unauthSet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedSet =
                readerClient.PostAsync("/organization/setName", createJsonContent (createOrganizationSetNameParameters ()))

            Assert.That(deniedSet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedSet =
                adminClient.PostAsync("/organization/setName", createJsonContent (createOrganizationSetNameParameters ()))

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

            let! unauthSet =
                unauthClient.PostAsync("/repository/setVisibility", createJsonContent (createRepositorySetVisibilityParameters repositoryId))

            Assert.That(unauthSet.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedSet =
                readerClient.PostAsync("/repository/setVisibility", createJsonContent (createRepositorySetVisibilityParameters repositoryId))

            Assert.That(deniedSet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedSet =
                adminClient.PostAsync("/repository/setVisibility", createJsonContent (createRepositorySetVisibilityParameters repositoryId))

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

            let! unauthResponse =
                unauthClient.PostAsync("/work/create", createJsonContent (createWorkItemParameters repositoryId))

            Assert.That(unauthResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedResponse =
                readerClient.PostAsync("/work/create", createJsonContent (createWorkItemParameters repositoryId))

            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedResponse =
                writerClient.PostAsync("/work/create", createJsonContent (createWorkItemParameters repositoryId))

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

            let! enableCommitResponse =
                adminClient.PostAsync("/branch/enableCommit", createJsonContent (createBranchEnableCommitParameters branch))

            Assert.That(enableCommitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! deniedEnable =
                writerClient.PostAsync("/branch/enableCommit", createJsonContent (createBranchEnableCommitParameters branch))

            Assert.That(deniedEnable.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! unauthCommit =
                unauthClient.PostAsync("/branch/commit", createJsonContent (createBranchCommitParameters branch))

            Assert.That(unauthCommit.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedCommit =
                unprivilegedClient.PostAsync("/branch/commit", createJsonContent (createBranchCommitParameters branch))

            Assert.That(deniedCommit.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedCommit =
                writerClient.PostAsync("/branch/commit", createJsonContent (createBranchCommitParameters branch))

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

            let! unauthDownload =
                unauthClient.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId))

            Assert.That(unauthDownload.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedDownload =
                unprivilegedClient.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId))

            Assert.That(deniedDownload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedDownload =
                readerClient.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId))

            Assert.That(allowedDownload.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! unauthUpload =
                unauthClient.PostAsync("/storage/getUploadUri", createJsonContent (createUploadParameters repositoryId))

            Assert.That(unauthUpload.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedUpload =
                unprivilegedClient.PostAsync("/storage/getUploadUri", createJsonContent (createUploadParameters repositoryId))

            Assert.That(deniedUpload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedUpload =
                writerClient.PostAsync("/storage/getUploadUri", createJsonContent (createUploadParameters repositoryId))

            Assert.That(allowedUpload.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }
