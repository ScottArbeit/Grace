namespace Grace.Server.Tests

open Azure.Storage.Blobs
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Artifact
open Grace.Types.PersonalAccessToken
open Grace.Types.Common
open Grace.Types.WorkItem
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

/// Groups shared helpers for work item integration helpers.
module private WorkItemIntegrationHelpers =
    /// Builds a deterministic authenticated client for integration setup fixture for the server integration work Item Integration assertions.
    let createAuthenticatedClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Builds a deterministic unauthenticated client for integration setup fixture for the server integration work Item Integration assertions.
    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

    /// Grants repo role needed by authorization-sensitive tests.
    let grantRepoRoleAsync (repositoryId: string) (principalId: string) (roleId: string) =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- "repo"
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authorize/grant-role", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
        }

    /// Builds a deterministic repository for integration setup fixture for the server integration work Item Integration assertions.
    let createRepositoryAsync (repositoryNamePrefix: string) =
        task {
            let repositoryId = Guid.NewGuid().ToString()
            let parameters = Parameters.Repository.CreateRepositoryParameters()

            let prefix =
                if String.IsNullOrWhiteSpace(repositoryNamePrefix) then
                    "repo"
                else
                    repositoryNamePrefix.Trim()

            let maxPrefixLength = 31

            let boundedPrefix =
                if prefix.Length > maxPrefixLength then
                    prefix.Substring(0, maxPrefixLength)
                else
                    prefix

            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"{boundedPrefix}-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/create", createJsonContent parameters)

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected repository create success but got {(int response.StatusCode)} {response.StatusCode}: {body}")
            else
                ()

            let storageConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageConnectionString)

            if not (String.IsNullOrWhiteSpace(storageConnectionString)) then
                let serviceClient = BlobServiceClient(storageConnectionString)
                let containerClient = serviceClient.GetBlobContainerClient(repositoryId.ToLowerInvariant())
                let! _ = containerClient.CreateIfNotExistsAsync()
                ()

            return repositoryId
        }

    /// Builds a deterministic work item with ID response for integration setup fixture for the server integration work Item Integration assertions.
    let createWorkItemWithIdResponseAsync (client: HttpClient) (repositoryId: string) (title: string) =
        task {
            let workItemId = Guid.NewGuid().ToString()
            let parameters = Parameters.WorkItem.CreateWorkItemParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemId
            parameters.Title <- title
            parameters.Description <- "integration test work item"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/work/create", createJsonContent parameters)
            return workItemId, response
        }

    /// Builds a deterministic work item response for integration setup fixture for the server integration work Item Integration assertions.
    let createWorkItemResponseAsync (client: HttpClient) (repositoryId: string) (title: string) =
        task {
            let! _, response = createWorkItemWithIdResponseAsync client repositoryId title
            return response
        }

    /// Builds a deterministic work item for integration setup fixture for the server integration work Item Integration assertions.
    let createWorkItemAsync (repositoryId: string) (title: string) =
        task {
            let! workItemId, response = createWorkItemWithIdResponseAsync Client repositoryId title
            response.EnsureSuccessStatusCode() |> ignore
            return workItemId
        }

    /// Defines update work item response behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let updateWorkItemResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let parameters = Parameters.WorkItem.UpdateWorkItemParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.Title <- $"Updated {Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/update", createJsonContent parameters)
        }

    /// Gets work item response from the running test server.
    let getWorkItemResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let parameters = Parameters.WorkItem.GetWorkItemParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/get", createJsonContent parameters)
        }

    /// Gets work item DTO from the running test server.
    let getWorkItemDtoAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let! response = getWorkItemResponseAsync client repositoryId workItemIdentifier
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<WorkItemDto>> response
            return returnValue.ReturnValue
        }

    /// Gets work item links from the running test server.
    let getWorkItemLinksAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let parameters = Parameters.WorkItem.GetWorkItemLinksParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/work/links/list", createJsonContent parameters)

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected links/list success but got {(int response.StatusCode)} {response.StatusCode}: {body}")
                return WorkItemLinksDto.Default
            else
                let! returnValue = deserializeContent<GraceReturnValue<WorkItemLinksDto>> response
                return returnValue.ReturnValue
        }

    /// Lists work item attachments response from the running test server.
    let listWorkItemAttachmentsResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let parameters = Parameters.WorkItem.ListWorkItemAttachmentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/attachments/list", createJsonContent parameters)
        }

    /// Lists work item attachments from the running test server.
    let listWorkItemAttachmentsAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let! response = listWorkItemAttachmentsResponseAsync client repositoryId workItemIdentifier

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected attachments/list success but got {(int response.StatusCode)} {response.StatusCode}: {body}")
                return Parameters.WorkItem.ListWorkItemAttachmentsResult()
            else
                let! returnValue = deserializeContent<GraceReturnValue<Parameters.WorkItem.ListWorkItemAttachmentsResult>> response
                return returnValue.ReturnValue
        }

    /// Defines show work item attachment response behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let showWorkItemAttachmentResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (attachmentType: string) (latest: bool) =
        task {
            let parameters = Parameters.WorkItem.ShowWorkItemAttachmentParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.AttachmentType <- attachmentType
            parameters.Latest <- latest
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/attachments/show", createJsonContent parameters)
        }

    /// Defines show work item attachment behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let showWorkItemAttachmentAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (attachmentType: string) (latest: bool) =
        task {
            let! response = showWorkItemAttachmentResponseAsync client repositoryId workItemIdentifier attachmentType latest

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected attachments/show success but got {(int response.StatusCode)} {response.StatusCode}: {body}")
                return Parameters.WorkItem.ShowWorkItemAttachmentResult()
            else
                let! returnValue = deserializeContent<GraceReturnValue<Parameters.WorkItem.ShowWorkItemAttachmentResult>> response
                return returnValue.ReturnValue
        }

    /// Downloads work item attachment response through storage test infrastructure.
    let downloadWorkItemAttachmentResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (artifactId: Guid) =
        task {
            let parameters = Parameters.WorkItem.DownloadWorkItemAttachmentParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ArtifactId <- artifactId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/attachments/download", createJsonContent parameters)
        }

    /// Downloads work item attachment through storage test infrastructure.
    let downloadWorkItemAttachmentAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (artifactId: Guid) =
        task {
            let! response = downloadWorkItemAttachmentResponseAsync client repositoryId workItemIdentifier artifactId

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected attachments/download success but got {(int response.StatusCode)} {response.StatusCode}: {body}")
                return Parameters.WorkItem.DownloadWorkItemAttachmentResult()
            else
                let! returnValue = deserializeContent<GraceReturnValue<Parameters.WorkItem.DownloadWorkItemAttachmentResult>> response
                return returnValue.ReturnValue
        }

    let addSummaryResponseAsync
        (client: HttpClient)
        (repositoryId: string)
        (workItemIdentifier: string)
        (summaryContent: string)
        (promptContent: string option)
        (promptOrigin: string option)
        (summaryArtifactIdOverride: string option)
        (correlationId: string)
        =
        task {
            let parameters = Parameters.WorkItem.AddSummaryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.SummaryContent <- summaryContent
            parameters.SummaryMimeType <- "text/markdown"
            parameters.PromptContent <- promptContent |> Option.defaultValue String.Empty
            parameters.PromptMimeType <- if promptContent.IsSome then "text/markdown" else String.Empty
            parameters.PromptOrigin <- promptOrigin |> Option.defaultValue String.Empty

            parameters.SummaryArtifactId <-
                summaryArtifactIdOverride
                |> Option.defaultValue String.Empty

            parameters.CorrelationId <- correlationId
            return! client.PostAsync("/work/add-summary", createJsonContent parameters)
        }

    let addSummaryAsync
        (client: HttpClient)
        (repositoryId: string)
        (workItemIdentifier: string)
        (summaryContent: string)
        (promptContent: string option)
        (promptOrigin: string option)
        (correlationId: string)
        =
        task {
            let! response = addSummaryResponseAsync client repositoryId workItemIdentifier summaryContent promptContent promptOrigin None correlationId

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected add-summary success but got {(int response.StatusCode)} {response.StatusCode}: {body}")
                return Parameters.WorkItem.AddSummaryResult()
            else
                let! returnValue = deserializeContent<GraceReturnValue<Parameters.WorkItem.AddSummaryResult>> response
                return returnValue.ReturnValue
        }

    /// Gets artifact download URI from the running test server.
    let getArtifactDownloadUriAsync (client: HttpClient) (repositoryId: string) (artifactId: Guid) =
        task {
            let correlationId = generateCorrelationId ()

            let route =
                $"/artifact/{artifactId}/download-uri?ownerId={ownerId}&organizationId={organizationId}&repositoryId={repositoryId}&correlationId={correlationId}"

            let! response = client.GetAsync(route)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<ArtifactDownloadUriResult>> response
            return returnValue.ReturnValue.DownloadUri
        }

    /// Gets artifact download URI response from the running test server.
    let getArtifactDownloadUriResponseAsync (client: HttpClient) (repositoryId: string) (artifactId: Guid) =
        task {
            let correlationId = generateCorrelationId ()

            let route =
                $"/artifact/{artifactId}/download-uri?ownerId={ownerId}&organizationId={organizationId}&repositoryId={repositoryId}&correlationId={correlationId}"

            return! client.GetAsync(route)
        }

    /// Defines link reference behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let linkReferenceAsync (repositoryId: string) (workItemIdentifier: string) (referenceId: Guid) =
        task {
            let parameters = Parameters.WorkItem.LinkReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ReferenceId <- referenceId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/work/link/reference", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    /// Defines link promotion set behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let linkPromotionSetAsync (repositoryId: string) (workItemIdentifier: string) (promotionSetId: Guid) =
        task {
            let parameters = Parameters.WorkItem.LinkPromotionSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.PromotionSetId <- promotionSetId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/work/link/promotion-set", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    let createArtifactResponseAsync
        (client: HttpClient)
        (repositoryId: string)
        (artifactType: string)
        (mimeType: string)
        (size: int64)
        (artifactId: Guid option)
        =
        task {
            let parameters = Parameters.Artifact.CreateArtifactParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId

            parameters.ArtifactId <-
                artifactId
                |> Option.map string
                |> Option.defaultValue String.Empty

            parameters.ArtifactType <- artifactType
            parameters.MimeType <- mimeType
            parameters.Size <- size
            parameters.Sha256 <- ""
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/artifact/create", createJsonContent parameters)
        }

    /// Builds a deterministic artifact for integration setup fixture for the server integration work Item Integration assertions.
    let createArtifactAsync (repositoryId: string) (artifactType: string) =
        task {
            let! response = createArtifactResponseAsync Client repositoryId artifactType "text/plain" 16L None
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<ArtifactCreateResult>> response
            return returnValue.ReturnValue.ArtifactId
        }

    let createArtifactResultAsync
        (client: HttpClient)
        (repositoryId: string)
        (artifactType: string)
        (mimeType: string)
        (payload: byte array)
        (artifactId: Guid option)
        =
        task {
            let! response = createArtifactResponseAsync client repositoryId artifactType mimeType (int64 payload.Length) artifactId

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync()
                Assert.Fail($"Expected artifact create success but got {response.StatusCode}: {body}")

            let! returnValue = deserializeContent<GraceReturnValue<ArtifactCreateResult>> response
            return returnValue.ReturnValue
        }

    /// Defines link artifact behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let linkArtifactAsync (repositoryId: string) (workItemIdentifier: string) (artifactId: Guid) =
        task {
            let parameters = Parameters.WorkItem.LinkArtifactParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ArtifactId <- artifactId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/work/link/artifact", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    /// Defines link artifact response behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let linkArtifactResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (artifactId: Guid) =
        task {
            let parameters = Parameters.WorkItem.LinkArtifactParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ArtifactId <- artifactId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/work/link/artifact", createJsonContent parameters)
        }

    /// Gets links response from the running test server.
    let getLinksResponseAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let parameters = Parameters.WorkItem.GetWorkItemLinksParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/work/links/list", createJsonContent parameters)
        }

    /// Defines remove reference link behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let removeReferenceLinkAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (referenceId: Guid) =
        task {
            let parameters = Parameters.WorkItem.RemoveReferenceLinkParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ReferenceId <- referenceId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/links/remove/reference", createJsonContent parameters)
        }

    /// Defines remove promotion set link behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let removePromotionSetLinkAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (promotionSetId: Guid) =
        task {
            let parameters = Parameters.WorkItem.RemovePromotionSetLinkParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.PromotionSetId <- promotionSetId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/links/remove/promotion-set", createJsonContent parameters)
        }

    /// Defines remove artifact link behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let removeArtifactLinkAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (artifactId: Guid) =
        task {
            let parameters = Parameters.WorkItem.RemoveArtifactLinkParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ArtifactId <- artifactId.ToString()
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/links/remove/artifact", createJsonContent parameters)
        }

    /// Defines remove artifact type links behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let removeArtifactTypeLinksAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) (artifactType: string) =
        task {
            let parameters = Parameters.WorkItem.RemoveArtifactTypeLinksParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.ArtifactType <- artifactType
            parameters.CorrelationId <- generateCorrelationId ()
            return! client.PostAsync("/work/links/remove/artifact-type", createJsonContent parameters)
        }

    /// Uploads bytes with SAS through storage test infrastructure.
    let uploadBytesWithSasAsync (uploadUri: Uri) (payload: byte array) =
        task {
            let blobClient = Azure.Storage.Blobs.Specialized.BlockBlobClient(uploadUri)
            use stream = new System.IO.MemoryStream(payload, writable = false)
            let options = Azure.Storage.Blobs.Models.BlobUploadOptions()
            let! _ = blobClient.UploadAsync(stream, options)
            ()
        }

    /// Downloads bytes with SAS through storage test infrastructure.
    let downloadBytesWithSasAsync (downloadUri: string) =
        task {
            let blobClient = BlobClient(Uri downloadUri)
            let! response = blobClient.DownloadContentAsync()
            return response.Value.Content.ToArray()
        }

    /// Builds a deterministic personal access token for integration setup fixture for the server integration work Item Integration assertions.
    let createPersonalAccessTokenAsync () =
        task {
            let parameters = Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"workitem-sdk-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authenticate/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            return returnValue.ReturnValue.Token
        }

    /// Defines configure SDK for server behavior for the surrounding tests used by the server integration work Item Integration scenario.
    let configureSdkForServerAsync () =
        task {
            let configuration = Current()
            configuration.ServerUri <- graceServerBaseAddress

            let! token = createPersonalAccessTokenAsync ()

            Grace.SDK.Auth.setTokenProvider (fun () -> task { return Some token })
        }

/// Covers work item number and links scenarios.
[<NonParallelizable>]
type WorkItemNumberAndLinksIntegrationTests() =

    /// Verifies the create then fetch by guid and number returns same work item scenario.
    [<Test>]
    member _.CreateThenFetchByGuidAndNumberReturnsSameWorkItem() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-id-number"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "id-number test"
            let! byId = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
            let! byNumber = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId (byId.WorkItemNumber.ToString())

            Assert.That(byNumber.WorkItemId, Is.EqualTo(byId.WorkItemId))
            Assert.That(byNumber.WorkItemNumber, Is.EqualTo(byId.WorkItemNumber))
            Assert.That(byNumber.Title, Is.EqualTo(byId.Title))
            Assert.That(byNumber.Description, Is.EqualTo(byId.Description))
        }

    /// Verifies the unknown numeric identifier returns not found error scenario.
    [<Test>]
    member _.UnknownNumericIdentifierReturnsNotFoundError() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-unknown-number"
            let! response = WorkItemIntegrationHelpers.getWorkItemResponseAsync Client repositoryId (Int64.MaxValue.ToString())

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response

            Assert.That(error.Error, Does.Contain(WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist))
        }

    /// Verifies the non positive numeric identifier returns validation error scenario.
    [<Test>]
    member _.NonPositiveNumericIdentifierReturnsValidationError() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-invalid-number"
            let! response = WorkItemIntegrationHelpers.getWorkItemResponseAsync Client repositoryId "0"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response

            Assert.That(error.Error, Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber))
        }

    /// Verifies the sequential creates produce unique monotonic numbers scenario.
    [<Test>]
    member _.SequentialCreatesProduceUniqueMonotonicNumbers() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-sequential"
            let createCount = 6
            let workItemIds = ResizeArray<string>()

            for index in 0 .. createCount - 1 do
                let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId $"sequential {index}"
                workItemIds.Add(workItemId)

            let! numbers =
                workItemIds.ToArray()
                |> Array.map (fun workItemId ->
                    task {
                        let! workItem = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
                        return workItem.WorkItemNumber
                    })
                |> Task.WhenAll

            Assert.That(numbers |> Array.distinct |> Array.length, Is.EqualTo(numbers.Length))

            let strictlyIncreasing =
                numbers
                |> Array.pairwise
                |> Array.forall (fun (first, second) -> second > first)

            Assert.That(strictlyIncreasing, Is.True)
        }

    /// Verifies the concurrent creates produce unique numbers without collisions scenario.
    [<Test>]
    member _.ConcurrentCreatesProduceUniqueNumbersWithoutCollisions() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-concurrent"
            let createCount = 24

            let! workItemIds =
                [|
                    for index in 0 .. createCount - 1 -> WorkItemIntegrationHelpers.createWorkItemAsync repositoryId $"concurrent {index}"
                |]
                |> Task.WhenAll

            let! numbers =
                workItemIds
                |> Array.map (fun workItemId ->
                    task {
                        let! workItem = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
                        return workItem.WorkItemNumber
                    })
                |> Task.WhenAll

            Assert.That(numbers.Length, Is.EqualTo(createCount))
            Assert.That(numbers |> Array.distinct |> Array.length, Is.EqualTo(createCount))
            Assert.That(numbers |> Array.forall (fun value -> value > 0L), Is.True)
        }

    /// Verifies the work item links lifecycle round trips across reference promotion set and artifacts scenario.
    [<Test>]
    member _.WorkItemLinksLifecycleRoundTripsAcrossReferencePromotionSetAndArtifacts() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-links-lifecycle"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "links lifecycle"
            let referenceId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            do! WorkItemIntegrationHelpers.linkReferenceAsync repositoryId workItemId referenceId
            do! WorkItemIntegrationHelpers.linkPromotionSetAsync repositoryId workItemId promotionSetId

            let! linkedBeforeArtifacts = WorkItemIntegrationHelpers.getWorkItemLinksAsync Client repositoryId workItemId

            Assert.That(linkedBeforeArtifacts.ReferenceIds, Has.Member(referenceId))
            Assert.That(linkedBeforeArtifacts.PromotionSetIds, Has.Member(promotionSetId))

            let summaryArtifactId = Guid.NewGuid()
            let promptArtifactId = Guid.NewGuid()
            let notesArtifactId = Guid.NewGuid()

            do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId summaryArtifactId
            do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId promptArtifactId
            do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId notesArtifactId

            let! removedReference = WorkItemIntegrationHelpers.removeReferenceLinkAsync Client repositoryId workItemId referenceId

            removedReference.EnsureSuccessStatusCode()
            |> ignore

            let! removedPromotionSet = WorkItemIntegrationHelpers.removePromotionSetLinkAsync Client repositoryId workItemId promotionSetId

            removedPromotionSet.EnsureSuccessStatusCode()
            |> ignore

            let! removedArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync Client repositoryId workItemId summaryArtifactId

            removedArtifact.EnsureSuccessStatusCode()
            |> ignore

            let! removedSummary = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync Client repositoryId workItemId "summary"
            removedSummary.EnsureSuccessStatusCode() |> ignore

            let! removedPrompt = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync Client repositoryId workItemId "prompt"
            removedPrompt.EnsureSuccessStatusCode() |> ignore

            let! removedNotes = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync Client repositoryId workItemId "notes"
            removedNotes.EnsureSuccessStatusCode() |> ignore

            let! removedPromptArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync Client repositoryId workItemId promptArtifactId

            removedPromptArtifact.EnsureSuccessStatusCode()
            |> ignore

            let! removedNotesArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync Client repositoryId workItemId notesArtifactId

            removedNotesArtifact.EnsureSuccessStatusCode()
            |> ignore

            let! afterRemoval = WorkItemIntegrationHelpers.getWorkItemLinksAsync Client repositoryId workItemId

            Assert.That(afterRemoval.ReferenceIds, Is.Empty)
            Assert.That(afterRemoval.PromotionSetIds, Is.Empty)
            Assert.That(afterRemoval.AgentSummaryArtifactIds, Is.Empty)
            Assert.That(afterRemoval.PromptArtifactIds, Is.Empty)
            Assert.That(afterRemoval.ReviewNotesArtifactIds, Is.Empty)
            Assert.That(afterRemoval.OtherArtifactIds, Is.Empty)
            Assert.That(afterRemoval.ArtifactIds, Is.Empty)
        }

/// Covers work item add summary scenarios.
[<NonParallelizable>]
type WorkItemAddSummaryIntegrationTests() =

    /// Verifies the add summary with guid creates summary link and download URI scenario.
    [<Test>]
    member _.AddSummaryWithGuidCreatesSummaryLinkAndDownloadUri() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-add-summary-guid"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "add-summary guid"
            let summaryContent = $"# Summary{Environment.NewLine}Guid flow replay validation"

            let! firstResult = WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId summaryContent None None (generateCorrelationId ())

            Assert.That(firstResult.WorkItemId, Is.EqualTo(workItemId))
            Assert.That(firstResult.PromptArtifactId, Is.EqualTo(String.Empty))

            let summaryArtifactId = Guid.Parse(firstResult.SummaryArtifactId)
            let! workItemByGuid = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
            let! workItemByNumber = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId (workItemByGuid.WorkItemNumber.ToString())

            Assert.That(workItemByGuid.ArtifactIds, Has.Member(summaryArtifactId))
            Assert.That(workItemByNumber.ArtifactIds, Has.Member(summaryArtifactId))

            let summaryArtifactCountByGuid =
                workItemByGuid.ArtifactIds
                |> List.filter (fun artifactId -> artifactId = summaryArtifactId)
                |> List.length

            Assert.That(summaryArtifactCountByGuid, Is.EqualTo(1))

            let! summaryDownloadUri = WorkItemIntegrationHelpers.getArtifactDownloadUriAsync Client repositoryId summaryArtifactId
            Assert.That(summaryDownloadUri, Is.Not.Null)
            Assert.That(String.IsNullOrWhiteSpace(summaryDownloadUri.AbsoluteUri), Is.False)
        }

    /// Verifies the add summary with work item number round trips prompt and links across both identifiers scenario.
    [<Test>]
    member _.AddSummaryWithWorkItemNumberRoundTripsPromptAndLinksAcrossBothIdentifiers() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-add-summary-number"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "add-summary number"
            let! workItem = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
            let workItemNumber = workItem.WorkItemNumber.ToString()
            let summaryContent = $"# Summary{Environment.NewLine}Number flow"
            let promptContent = $"# Prompt{Environment.NewLine}Number flow"

            let! addSummaryResult =
                WorkItemIntegrationHelpers.addSummaryAsync
                    Client
                    repositoryId
                    workItemNumber
                    summaryContent
                    (Some promptContent)
                    (Some "agent://codex")
                    (generateCorrelationId ())

            let summaryArtifactId = Guid.Parse(addSummaryResult.SummaryArtifactId)
            let promptArtifactId = Guid.Parse(addSummaryResult.PromptArtifactId)

            let! workItemByNumber = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemNumber
            let! workItemByGuid = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId

            Assert.That(workItemByNumber.WorkItemId, Is.EqualTo(Guid.Parse(workItemId)))
            Assert.That(workItemByGuid.WorkItemId, Is.EqualTo(Guid.Parse(workItemId)))
            Assert.That(workItemByNumber.WorkItemNumber, Is.EqualTo(workItem.WorkItemNumber))
            Assert.That(workItemByGuid.WorkItemNumber, Is.EqualTo(workItem.WorkItemNumber))

            Assert.That(workItemByNumber.ArtifactIds, Has.Member(summaryArtifactId))
            Assert.That(workItemByNumber.ArtifactIds, Has.Member(promptArtifactId))
            Assert.That(workItemByGuid.ArtifactIds, Has.Member(summaryArtifactId))
            Assert.That(workItemByGuid.ArtifactIds, Has.Member(promptArtifactId))

            let! summaryDownloadUri = WorkItemIntegrationHelpers.getArtifactDownloadUriAsync Client repositoryId summaryArtifactId
            let! promptDownloadUri = WorkItemIntegrationHelpers.getArtifactDownloadUriAsync Client repositoryId promptArtifactId

            Assert.That(summaryDownloadUri, Is.Not.Null)
            Assert.That(promptDownloadUri, Is.Not.Null)
            Assert.That(String.IsNullOrWhiteSpace(summaryDownloadUri.AbsoluteUri), Is.False)
            Assert.That(String.IsNullOrWhiteSpace(promptDownloadUri.AbsoluteUri), Is.False)
        }

    /// Verifies the add summary rejects caller supplied artifact IDs for numeric identifiers scenario.
    [<Test>]
    member _.AddSummaryRejectsCallerSuppliedArtifactIdsForNumericIdentifiers() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-add-summary-reject-artifact-id"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "reject caller supplied artifact id"
            let! workItem = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
            let workItemNumber = workItem.WorkItemNumber.ToString()

            let! response =
                WorkItemIntegrationHelpers.addSummaryResponseAsync
                    Client
                    repositoryId
                    workItemNumber
                    "summary content"
                    None
                    None
                    (Some(Guid.NewGuid().ToString()))
                    (generateCorrelationId ())

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! graceError = deserializeContent<GraceError> response

            Assert.That(graceError.Error, Does.Contain("Caller-supplied artifact IDs are not supported by add-summary"))
            Assert.That(graceError.Error, Does.Contain("Canonical add-summary requests must provide SummaryContent"))
        }

/// Covers work item attachment endpoints scenarios.
[<NonParallelizable>]
type WorkItemAttachmentEndpointsIntegrationTests() =

    /// Verifies the attachment list supports guid and number and filters to reviewer attachment types scenario.
    [<Test>]
    member _.AttachmentListSupportsGuidAndNumberAndFiltersToReviewerAttachmentTypes() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-attachments-list"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "attachments list"

            let! addSummaryResult =
                WorkItemIntegrationHelpers.addSummaryAsync
                    Client
                    repositoryId
                    workItemId
                    "summary list content"
                    (Some "prompt list content")
                    None
                    (generateCorrelationId ())

            let! otherArtifactId = WorkItemIntegrationHelpers.createArtifactAsync repositoryId "Other"
            do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId otherArtifactId

            let! workItemByGuid = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
            let workItemNumber = workItemByGuid.WorkItemNumber.ToString()

            let! attachmentsByGuid = WorkItemIntegrationHelpers.listWorkItemAttachmentsAsync Client repositoryId workItemId
            let! attachmentsByNumber = WorkItemIntegrationHelpers.listWorkItemAttachmentsAsync Client repositoryId workItemNumber

            Assert.That(attachmentsByGuid.WorkItemId, Is.EqualTo(workItemId))
            Assert.That(attachmentsByGuid.WorkItemNumber, Is.EqualTo(workItemByGuid.WorkItemNumber))
            Assert.That(attachmentsByNumber.WorkItemId, Is.EqualTo(workItemId))
            Assert.That(attachmentsByNumber.WorkItemNumber, Is.EqualTo(workItemByGuid.WorkItemNumber))

            let attachmentIdsByGuid =
                attachmentsByGuid.Attachments
                |> Seq.map (fun attachment -> attachment.ArtifactId)
                |> Seq.toArray

            let attachmentIdsByNumber =
                attachmentsByNumber.Attachments
                |> Seq.map (fun attachment -> attachment.ArtifactId)
                |> Seq.toArray

            Assert.That(attachmentIdsByGuid, Is.EquivalentTo(attachmentIdsByNumber))
            Assert.That(attachmentIdsByGuid, Has.Member(addSummaryResult.SummaryArtifactId))
            Assert.That(attachmentIdsByGuid.Length, Is.GreaterThanOrEqualTo(1))
        }

    /// Verifies the attachment show selects deterministic latest or earliest by attachment type scenario.
    [<Test>]
    member _.AttachmentShowSelectsDeterministicLatestOrEarliestByAttachmentType() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-attachments-show"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "attachments show"
            let firstSummaryContent = "first summary content"
            let secondSummaryContent = "second summary content"

            let! firstResult =
                WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId firstSummaryContent None None (generateCorrelationId ())

            do! Task.Delay(20)

            let! secondResult =
                WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId secondSummaryContent None None (generateCorrelationId ())

            let! workItemByGuid = WorkItemIntegrationHelpers.getWorkItemDtoAsync Client repositoryId workItemId
            let workItemNumber = workItemByGuid.WorkItemNumber.ToString()
            let! attachmentList = WorkItemIntegrationHelpers.listWorkItemAttachmentsAsync Client repositoryId workItemId

            let orderedAttachmentIds =
                attachmentList.Attachments
                |> Seq.map (fun attachment -> attachment.ArtifactId)
                |> Seq.toArray

            let! showEarliest = WorkItemIntegrationHelpers.showWorkItemAttachmentAsync Client repositoryId workItemId "summary" false

            let! showLatest = WorkItemIntegrationHelpers.showWorkItemAttachmentAsync Client repositoryId workItemNumber "summary" true
            let! showLatestAgain = WorkItemIntegrationHelpers.showWorkItemAttachmentAsync Client repositoryId workItemNumber "summary" true

            Assert.That(orderedAttachmentIds.Length, Is.GreaterThanOrEqualTo(1))
            Assert.That(String.IsNullOrWhiteSpace(showEarliest.ArtifactId), Is.False)
            Assert.That(showEarliest.SelectedUsingLatest, Is.False)
            Assert.That(showEarliest.AvailableAttachmentCount, Is.GreaterThanOrEqualTo(1))

            Assert.That(showLatest.ArtifactId, Is.EqualTo(showLatestAgain.ArtifactId))
            Assert.That(showLatest.SelectedUsingLatest, Is.True)
            Assert.That(showLatest.AvailableAttachmentCount, Is.GreaterThanOrEqualTo(1))
        }

    /// Verifies the attachment download returns download URI for linked reviewer attachment and rejects invalid artifacts scenario.
    [<Test>]
    member _.AttachmentDownloadReturnsDownloadUriForLinkedReviewerAttachmentAndRejectsInvalidArtifacts() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-attachments-download"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "attachments download"
            let summaryContent = "download summary content"

            let! addSummaryResult =
                WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId summaryContent None None (generateCorrelationId ())

            let! linkedOtherArtifactId = WorkItemIntegrationHelpers.createArtifactAsync repositoryId "Other"
            do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId linkedOtherArtifactId

            let summaryArtifactId = Guid.Parse(addSummaryResult.SummaryArtifactId)
            let! downloadResult = WorkItemIntegrationHelpers.downloadWorkItemAttachmentAsync Client repositoryId workItemId summaryArtifactId

            Assert.That(downloadResult.ArtifactId, Is.EqualTo(summaryArtifactId.ToString()))
            Assert.That(downloadResult.AttachmentType, Is.EqualTo("summary"))
            Assert.That(downloadResult.MimeType, Is.EqualTo("text/markdown"))
            Assert.That(downloadResult.Size, Is.EqualTo(Encoding.UTF8.GetByteCount(summaryContent)))
            Assert.That(String.IsNullOrWhiteSpace(downloadResult.DownloadUri), Is.False)

            let! downloadedBytes = WorkItemIntegrationHelpers.downloadBytesWithSasAsync downloadResult.DownloadUri
            let downloadedContent = Encoding.UTF8.GetString(downloadedBytes)

            Assert.That(downloadedContent, Is.EqualTo(summaryContent))

            let! notLinkedResponse = WorkItemIntegrationHelpers.downloadWorkItemAttachmentResponseAsync Client repositoryId workItemId (Guid.NewGuid())

            Assert.That(notLinkedResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! notLinkedError = deserializeContent<GraceError> notLinkedResponse
            Assert.That(notLinkedError.Error, Does.Contain("not linked"))
        }

/// Covers work item links authorization scenarios.
[<Parallelizable(ParallelScope.All)>]
type WorkItemLinksAuthorizationIntegrationTests() =

    /// Verifies the work item and attachment routes enforce repository permissions by role and repository scope scenario.
    [<Test>]
    member _.WorkItemAndAttachmentRoutesEnforceRepositoryPermissionsByRoleAndRepositoryScope() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-permission-target"
            let! otherRepositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-permission-other"
            let repoReader = $"{Guid.NewGuid()}"
            let repoWriter = $"{Guid.NewGuid()}"
            let repoAdmin = $"{Guid.NewGuid()}"
            let crossRepoPrincipal = $"{Guid.NewGuid()}"

            do! WorkItemIntegrationHelpers.grantRepoRoleAsync repositoryId repoReader "RepositoryReader"
            do! WorkItemIntegrationHelpers.grantRepoRoleAsync repositoryId repoWriter "RepositoryContributor"
            do! WorkItemIntegrationHelpers.grantRepoRoleAsync repositoryId repoAdmin "RepositoryAdmin"
            do! WorkItemIntegrationHelpers.grantRepoRoleAsync otherRepositoryId crossRepoPrincipal "RepositoryAdmin"

            use unauthenticatedClient = WorkItemIntegrationHelpers.createUnauthenticatedClient ()
            use noRoleClient = WorkItemIntegrationHelpers.createAuthenticatedClient $"{Guid.NewGuid()}"
            use readerClient = WorkItemIntegrationHelpers.createAuthenticatedClient repoReader
            use writerClient = WorkItemIntegrationHelpers.createAuthenticatedClient repoWriter
            use adminClient = WorkItemIntegrationHelpers.createAuthenticatedClient repoAdmin
            use crossRepoClient = WorkItemIntegrationHelpers.createAuthenticatedClient crossRepoPrincipal

            let! unauthenticatedCreate = WorkItemIntegrationHelpers.createWorkItemResponseAsync unauthenticatedClient repositoryId "unauth create"
            Assert.That(unauthenticatedCreate.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! noRoleCreate = WorkItemIntegrationHelpers.createWorkItemResponseAsync noRoleClient repositoryId "no role create"
            Assert.That(noRoleCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerCreate = WorkItemIntegrationHelpers.createWorkItemResponseAsync readerClient repositoryId "reader create"
            Assert.That(readerCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoCreate = WorkItemIntegrationHelpers.createWorkItemResponseAsync crossRepoClient repositoryId "cross repo create"
            Assert.That(crossRepoCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! workItemId, writerCreate = WorkItemIntegrationHelpers.createWorkItemWithIdResponseAsync writerClient repositoryId "writer create"
            Assert.That(writerCreate.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! createdWorkItem = WorkItemIntegrationHelpers.getWorkItemDtoAsync writerClient repositoryId workItemId
            let workItemNumber = createdWorkItem.WorkItemNumber.ToString()

            let! adminCreate = WorkItemIntegrationHelpers.createWorkItemResponseAsync adminClient repositoryId "admin create"
            Assert.That(adminCreate.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! noRoleGet = WorkItemIntegrationHelpers.getWorkItemResponseAsync noRoleClient repositoryId workItemId
            Assert.That(noRoleGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoGet = WorkItemIntegrationHelpers.getWorkItemResponseAsync crossRepoClient repositoryId workItemId
            Assert.That(crossRepoGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerGet = WorkItemIntegrationHelpers.getWorkItemResponseAsync readerClient repositoryId workItemNumber
            Assert.That(readerGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! writerGet = WorkItemIntegrationHelpers.getWorkItemResponseAsync writerClient repositoryId workItemId
            Assert.That(writerGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! adminGet = WorkItemIntegrationHelpers.getWorkItemResponseAsync adminClient repositoryId workItemId
            Assert.That(adminGet.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! readerUpdate = WorkItemIntegrationHelpers.updateWorkItemResponseAsync readerClient repositoryId workItemId
            Assert.That(readerUpdate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoUpdate = WorkItemIntegrationHelpers.updateWorkItemResponseAsync crossRepoClient repositoryId workItemId
            Assert.That(crossRepoUpdate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! writerUpdate = WorkItemIntegrationHelpers.updateWorkItemResponseAsync writerClient repositoryId workItemId
            Assert.That(writerUpdate.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! adminUpdate = WorkItemIntegrationHelpers.updateWorkItemResponseAsync adminClient repositoryId workItemId
            Assert.That(adminUpdate.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let linkedArtifactId = Guid.NewGuid()
            let! readerLink = WorkItemIntegrationHelpers.linkArtifactResponseAsync readerClient repositoryId workItemId linkedArtifactId
            Assert.That(readerLink.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoLink = WorkItemIntegrationHelpers.linkArtifactResponseAsync crossRepoClient repositoryId workItemId linkedArtifactId
            Assert.That(crossRepoLink.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! writerLink = WorkItemIntegrationHelpers.linkArtifactResponseAsync writerClient repositoryId workItemId linkedArtifactId
            Assert.That(writerLink.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! linksForReader = WorkItemIntegrationHelpers.getLinksResponseAsync readerClient repositoryId workItemId
            Assert.That(linksForReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! linksForNoRole = WorkItemIntegrationHelpers.getLinksResponseAsync noRoleClient repositoryId workItemId
            Assert.That(linksForNoRole.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! linksForCrossRepo = WorkItemIntegrationHelpers.getLinksResponseAsync crossRepoClient repositoryId workItemId
            Assert.That(linksForCrossRepo.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let summaryContent = "permission matrix summary"

            let! readerAddSummary =
                WorkItemIntegrationHelpers.addSummaryResponseAsync readerClient repositoryId workItemId summaryContent None None None (generateCorrelationId ())

            Assert.That(readerAddSummary.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoAddSummary =
                WorkItemIntegrationHelpers.addSummaryResponseAsync
                    crossRepoClient
                    repositoryId
                    workItemId
                    summaryContent
                    None
                    None
                    None
                    (generateCorrelationId ())

            Assert.That(crossRepoAddSummary.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! addedSummary =
                WorkItemIntegrationHelpers.addSummaryAsync writerClient repositoryId workItemId summaryContent None None (generateCorrelationId ())

            let summaryArtifactId = Guid.Parse(addedSummary.SummaryArtifactId)

            let! readerAttachments = WorkItemIntegrationHelpers.listWorkItemAttachmentsResponseAsync readerClient repositoryId workItemNumber
            Assert.That(readerAttachments.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! noRoleAttachments = WorkItemIntegrationHelpers.listWorkItemAttachmentsResponseAsync noRoleClient repositoryId workItemId
            Assert.That(noRoleAttachments.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoAttachments = WorkItemIntegrationHelpers.listWorkItemAttachmentsResponseAsync crossRepoClient repositoryId workItemId
            Assert.That(crossRepoAttachments.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerShow = WorkItemIntegrationHelpers.showWorkItemAttachmentResponseAsync readerClient repositoryId workItemNumber "summary" true
            Assert.That(readerShow.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! noRoleShow = WorkItemIntegrationHelpers.showWorkItemAttachmentResponseAsync noRoleClient repositoryId workItemId "summary" true
            Assert.That(noRoleShow.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerDownload = WorkItemIntegrationHelpers.downloadWorkItemAttachmentResponseAsync readerClient repositoryId workItemId summaryArtifactId
            Assert.That(readerDownload.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! noRoleDownload = WorkItemIntegrationHelpers.downloadWorkItemAttachmentResponseAsync noRoleClient repositoryId workItemId summaryArtifactId
            Assert.That(noRoleDownload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerRemoveReference = WorkItemIntegrationHelpers.removeReferenceLinkAsync readerClient repositoryId workItemId (Guid.NewGuid())
            Assert.That(readerRemoveReference.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! writerRemoveReference = WorkItemIntegrationHelpers.removeReferenceLinkAsync writerClient repositoryId workItemId (Guid.NewGuid())
            Assert.That(writerRemoveReference.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! adminRemoveArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync adminClient repositoryId workItemId linkedArtifactId
            Assert.That(adminRemoveArtifact.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    /// Verifies the artifact routes enforce repository permissions and preserve download identity path and bytes scenario.
    [<Test>]
    member _.ArtifactRoutesEnforceRepositoryPermissionsAndPreserveDownloadIdentityPathAndBytes() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "artifact-permission-target"
            let! otherRepositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "artifact-permission-other"
            let repoReader = $"{Guid.NewGuid()}"
            let repoWriter = $"{Guid.NewGuid()}"
            let crossRepoPrincipal = $"{Guid.NewGuid()}"

            do! WorkItemIntegrationHelpers.grantRepoRoleAsync repositoryId repoReader "RepositoryReader"
            do! WorkItemIntegrationHelpers.grantRepoRoleAsync repositoryId repoWriter "RepositoryContributor"
            do! WorkItemIntegrationHelpers.grantRepoRoleAsync otherRepositoryId crossRepoPrincipal "RepositoryAdmin"

            use unauthenticatedClient = WorkItemIntegrationHelpers.createUnauthenticatedClient ()
            use noRoleClient = WorkItemIntegrationHelpers.createAuthenticatedClient $"{Guid.NewGuid()}"
            use readerClient = WorkItemIntegrationHelpers.createAuthenticatedClient repoReader
            use writerClient = WorkItemIntegrationHelpers.createAuthenticatedClient repoWriter
            use crossRepoClient = WorkItemIntegrationHelpers.createAuthenticatedClient crossRepoPrincipal

            let payload = Encoding.UTF8.GetBytes("artifact permission payload")
            let artifactId = Guid.NewGuid()

            let! unauthenticatedCreate =
                WorkItemIntegrationHelpers.createArtifactResponseAsync
                    unauthenticatedClient
                    repositoryId
                    "ValidationOutput"
                    "text/plain"
                    (int64 payload.Length)
                    (Some artifactId)

            Assert.That(unauthenticatedCreate.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! noRoleCreate =
                WorkItemIntegrationHelpers.createArtifactResponseAsync
                    noRoleClient
                    repositoryId
                    "ValidationOutput"
                    "text/plain"
                    (int64 payload.Length)
                    (Some artifactId)

            Assert.That(noRoleCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! readerCreate =
                WorkItemIntegrationHelpers.createArtifactResponseAsync
                    readerClient
                    repositoryId
                    "ValidationOutput"
                    "text/plain"
                    (int64 payload.Length)
                    (Some artifactId)

            Assert.That(readerCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoCreate =
                WorkItemIntegrationHelpers.createArtifactResponseAsync
                    crossRepoClient
                    repositoryId
                    "ValidationOutput"
                    "text/plain"
                    (int64 payload.Length)
                    (Some artifactId)

            Assert.That(crossRepoCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! createResult =
                WorkItemIntegrationHelpers.createArtifactResultAsync writerClient repositoryId "ValidationOutput" "text/plain" payload (Some artifactId)

            Assert.That(createResult.ArtifactId, Is.EqualTo(artifactId))
            Assert.That(createResult.BlobPath, Does.Contain(artifactId.ToString()))
            Assert.That(createResult.BlobPath, Does.StartWith("grace-artifacts/"))

            do! WorkItemIntegrationHelpers.uploadBytesWithSasAsync createResult.UploadUri payload

            let! noRoleDownload = WorkItemIntegrationHelpers.getArtifactDownloadUriResponseAsync noRoleClient repositoryId artifactId
            Assert.That(noRoleDownload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! crossRepoDownload = WorkItemIntegrationHelpers.getArtifactDownloadUriResponseAsync crossRepoClient repositoryId artifactId
            Assert.That(crossRepoDownload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! mismatchedScopeDownload = WorkItemIntegrationHelpers.getArtifactDownloadUriResponseAsync crossRepoClient otherRepositoryId artifactId
            Assert.That(mismatchedScopeDownload.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let! mismatchedScopeBody = mismatchedScopeDownload.Content.ReadAsStringAsync()
            Assert.That(mismatchedScopeBody, Does.Contain(ArtifactError.getErrorMessage ArtifactError.ArtifactDoesNotExist))

            let! readerDownloadResponse = WorkItemIntegrationHelpers.getArtifactDownloadUriResponseAsync readerClient repositoryId artifactId
            Assert.That(readerDownloadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! readerDownloadReturnValue = deserializeContent<GraceReturnValue<ArtifactDownloadUriResult>> readerDownloadResponse
            Assert.That(readerDownloadReturnValue.ReturnValue.ArtifactId, Is.EqualTo(artifactId))

            let downloadUri = readerDownloadReturnValue.ReturnValue.DownloadUri
            Assert.That(String.IsNullOrWhiteSpace(downloadUri.AbsoluteUri), Is.False)

            let! downloadedBytes = WorkItemIntegrationHelpers.downloadBytesWithSasAsync downloadUri.AbsoluteUri
            Assert.That(Convert.ToHexString(downloadedBytes), Is.EqualTo(Convert.ToHexString(payload)))
        }

/// Covers work item SDK smoke scenarios.
[<NonParallelizable>]
type WorkItemSdkSmokeIntegrationTests() =

    /// Runs with SDK authentication with the configured test context.
    let runWithSdkAuthentication (testBody: unit -> Task<unit>) =
        task {
            do! WorkItemIntegrationHelpers.configureSdkForServerAsync ()

            try
                do! testBody ()
            finally
                Grace.SDK.Auth.clearTokenProvider ()
        }

    /// Verifies the SDK work item link apis round trip scenario.
    [<Test>]
    member _.SdkWorkItemLinkApisRoundTrip() =
        task {
            do!
                runWithSdkAuthentication (fun () ->
                    task {
                        let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-sdk-roundtrip"
                        let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "sdk links"
                        let referenceId = Guid.NewGuid()
                        let promotionSetId = Guid.NewGuid()

                        do! WorkItemIntegrationHelpers.linkReferenceAsync repositoryId workItemId referenceId
                        do! WorkItemIntegrationHelpers.linkPromotionSetAsync repositoryId workItemId promotionSetId

                        let linksParameters = Parameters.WorkItem.GetWorkItemLinksParameters()
                        linksParameters.OwnerId <- ownerId
                        linksParameters.OrganizationId <- organizationId
                        linksParameters.RepositoryId <- repositoryId
                        linksParameters.WorkItemId <- workItemId
                        linksParameters.CorrelationId <- generateCorrelationId ()

                        let! linksResult = Grace.SDK.WorkItem.GetLinks linksParameters

                        let linked =
                            match linksResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected SDK GetLinks success but got error: {error.Error}")
                                WorkItemLinksDto.Default

                        Assert.That(linked.ReferenceIds, Has.Member(referenceId))
                        Assert.That(linked.PromotionSetIds, Has.Member(promotionSetId))

                        let summaryArtifactId = Guid.NewGuid()
                        let promptArtifactId = Guid.NewGuid()
                        let notesArtifactId = Guid.NewGuid()

                        do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId summaryArtifactId
                        do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId promptArtifactId
                        do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId notesArtifactId

                        let removeReferenceParameters = Parameters.WorkItem.RemoveReferenceLinkParameters()
                        removeReferenceParameters.OwnerId <- ownerId
                        removeReferenceParameters.OrganizationId <- organizationId
                        removeReferenceParameters.RepositoryId <- repositoryId
                        removeReferenceParameters.WorkItemId <- workItemId
                        removeReferenceParameters.ReferenceId <- referenceId.ToString()
                        removeReferenceParameters.CorrelationId <- generateCorrelationId ()

                        let! removeReferenceResult = Grace.SDK.WorkItem.RemoveReferenceLink removeReferenceParameters
                        Assert.That(removeReferenceResult.IsOk, Is.True)

                        let removePromotionParameters = Parameters.WorkItem.RemovePromotionSetLinkParameters()
                        removePromotionParameters.OwnerId <- ownerId
                        removePromotionParameters.OrganizationId <- organizationId
                        removePromotionParameters.RepositoryId <- repositoryId
                        removePromotionParameters.WorkItemId <- workItemId
                        removePromotionParameters.PromotionSetId <- promotionSetId.ToString()
                        removePromotionParameters.CorrelationId <- generateCorrelationId ()

                        let! removePromotionResult = Grace.SDK.WorkItem.RemovePromotionSetLink removePromotionParameters
                        Assert.That(removePromotionResult.IsOk, Is.True)

                        let removeArtifactParameters = Parameters.WorkItem.RemoveArtifactLinkParameters()
                        removeArtifactParameters.OwnerId <- ownerId
                        removeArtifactParameters.OrganizationId <- organizationId
                        removeArtifactParameters.RepositoryId <- repositoryId
                        removeArtifactParameters.WorkItemId <- workItemId
                        removeArtifactParameters.ArtifactId <- summaryArtifactId.ToString()
                        removeArtifactParameters.CorrelationId <- generateCorrelationId ()

                        let! removeArtifactResult = Grace.SDK.WorkItem.RemoveArtifactLink removeArtifactParameters
                        Assert.That(removeArtifactResult.IsOk, Is.True)

                        let removeSummaryParameters = Parameters.WorkItem.RemoveArtifactTypeLinksParameters()
                        removeSummaryParameters.OwnerId <- ownerId
                        removeSummaryParameters.OrganizationId <- organizationId
                        removeSummaryParameters.RepositoryId <- repositoryId
                        removeSummaryParameters.WorkItemId <- workItemId
                        removeSummaryParameters.ArtifactType <- "summary"
                        removeSummaryParameters.CorrelationId <- generateCorrelationId ()

                        let! removeSummaryResult = Grace.SDK.WorkItem.RemoveArtifactTypeLinks removeSummaryParameters
                        Assert.That(removeSummaryResult.IsOk, Is.True)

                        let removePromptParameters = Parameters.WorkItem.RemoveArtifactTypeLinksParameters()
                        removePromptParameters.OwnerId <- ownerId
                        removePromptParameters.OrganizationId <- organizationId
                        removePromptParameters.RepositoryId <- repositoryId
                        removePromptParameters.WorkItemId <- workItemId
                        removePromptParameters.ArtifactType <- "prompt"
                        removePromptParameters.CorrelationId <- generateCorrelationId ()

                        let! removePromptResult = Grace.SDK.WorkItem.RemoveArtifactTypeLinks removePromptParameters
                        Assert.That(removePromptResult.IsOk, Is.True)

                        let removeNotesParameters = Parameters.WorkItem.RemoveArtifactTypeLinksParameters()
                        removeNotesParameters.OwnerId <- ownerId
                        removeNotesParameters.OrganizationId <- organizationId
                        removeNotesParameters.RepositoryId <- repositoryId
                        removeNotesParameters.WorkItemId <- workItemId
                        removeNotesParameters.ArtifactType <- "notes"
                        removeNotesParameters.CorrelationId <- generateCorrelationId ()

                        let! removeNotesResult = Grace.SDK.WorkItem.RemoveArtifactTypeLinks removeNotesParameters
                        Assert.That(removeNotesResult.IsOk, Is.True)

                        let removePromptArtifactParameters = Parameters.WorkItem.RemoveArtifactLinkParameters()
                        removePromptArtifactParameters.OwnerId <- ownerId
                        removePromptArtifactParameters.OrganizationId <- organizationId
                        removePromptArtifactParameters.RepositoryId <- repositoryId
                        removePromptArtifactParameters.WorkItemId <- workItemId
                        removePromptArtifactParameters.ArtifactId <- promptArtifactId.ToString()
                        removePromptArtifactParameters.CorrelationId <- generateCorrelationId ()

                        let! removePromptArtifactResult = Grace.SDK.WorkItem.RemoveArtifactLink removePromptArtifactParameters
                        Assert.That(removePromptArtifactResult.IsOk, Is.True)

                        let removeNotesArtifactParameters = Parameters.WorkItem.RemoveArtifactLinkParameters()
                        removeNotesArtifactParameters.OwnerId <- ownerId
                        removeNotesArtifactParameters.OrganizationId <- organizationId
                        removeNotesArtifactParameters.RepositoryId <- repositoryId
                        removeNotesArtifactParameters.WorkItemId <- workItemId
                        removeNotesArtifactParameters.ArtifactId <- notesArtifactId.ToString()
                        removeNotesArtifactParameters.CorrelationId <- generateCorrelationId ()

                        let! removeNotesArtifactResult = Grace.SDK.WorkItem.RemoveArtifactLink removeNotesArtifactParameters
                        Assert.That(removeNotesArtifactResult.IsOk, Is.True)

                        let! linksAfterResult = Grace.SDK.WorkItem.GetLinks linksParameters

                        let afterRemoval =
                            match linksAfterResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected SDK GetLinks success but got error after removals: {error.Error}")
                                WorkItemLinksDto.Default

                        Assert.That(afterRemoval.ReferenceIds, Is.Empty)
                        Assert.That(afterRemoval.PromotionSetIds, Is.Empty)
                        Assert.That(afterRemoval.AgentSummaryArtifactIds, Is.Empty)
                        Assert.That(afterRemoval.PromptArtifactIds, Is.Empty)
                        Assert.That(afterRemoval.ReviewNotesArtifactIds, Is.Empty)
                        Assert.That(afterRemoval.OtherArtifactIds, Is.Empty)
                    })
        }

    /// Verifies the SDK work item attachment apis support list show and download scenario.
    [<Test>]
    member _.SdkWorkItemAttachmentApisSupportListShowAndDownload() =
        task {
            do!
                runWithSdkAuthentication (fun () ->
                    task {
                        let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-sdk-attachments"
                        let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "sdk attachments"

                        let firstSummaryContent = "sdk summary one"
                        let secondSummaryContent = "sdk summary two"

                        let! firstSummary =
                            WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId firstSummaryContent None None (generateCorrelationId ())

                        do! Task.Delay(20)

                        let! secondSummary =
                            WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId secondSummaryContent None None (generateCorrelationId ())

                        let listParameters = Parameters.WorkItem.ListWorkItemAttachmentsParameters()
                        listParameters.OwnerId <- ownerId
                        listParameters.OrganizationId <- organizationId
                        listParameters.RepositoryId <- repositoryId
                        listParameters.WorkItemId <- workItemId
                        listParameters.CorrelationId <- generateCorrelationId ()

                        let! listResult = Grace.SDK.WorkItem.ListAttachments listParameters

                        let attachments =
                            match listResult with
                            | Ok returnValue -> returnValue.ReturnValue.Attachments |> Seq.toArray
                            | Error error ->
                                Assert.Fail($"Expected SDK ListAttachments success but got error: {error.Error}")
                                Array.empty

                        Assert.That(
                            attachments
                            |> Array.map (fun attachment -> attachment.ArtifactId),
                            Has.Member(firstSummary.SummaryArtifactId)
                        )

                        Assert.That(
                            attachments
                            |> Array.map (fun attachment -> attachment.ArtifactId),
                            Has.Member(secondSummary.SummaryArtifactId)
                        )

                        let orderedAttachmentIds =
                            attachments
                            |> Array.map (fun attachment -> attachment.ArtifactId)

                        Assert.That(orderedAttachmentIds.Length, Is.GreaterThanOrEqualTo(1))

                        let showParameters = Parameters.WorkItem.ShowWorkItemAttachmentParameters()
                        showParameters.OwnerId <- ownerId
                        showParameters.OrganizationId <- organizationId
                        showParameters.RepositoryId <- repositoryId
                        showParameters.WorkItemId <- workItemId
                        showParameters.AttachmentType <- "summary"
                        showParameters.Latest <- true
                        showParameters.CorrelationId <- generateCorrelationId ()

                        let! showResult = Grace.SDK.WorkItem.ShowAttachment showParameters

                        let shownAttachment =
                            match showResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected SDK ShowAttachment success but got error: {error.Error}")
                                Parameters.WorkItem.ShowWorkItemAttachmentResult()

                        Assert.That(String.IsNullOrWhiteSpace(shownAttachment.ArtifactId), Is.False)

                        let downloadParameters = Parameters.WorkItem.DownloadWorkItemAttachmentParameters()
                        downloadParameters.OwnerId <- ownerId
                        downloadParameters.OrganizationId <- organizationId
                        downloadParameters.RepositoryId <- repositoryId
                        downloadParameters.WorkItemId <- workItemId
                        downloadParameters.ArtifactId <- shownAttachment.ArtifactId
                        downloadParameters.CorrelationId <- generateCorrelationId ()

                        let! downloadResult = Grace.SDK.WorkItem.DownloadAttachment downloadParameters

                        let download =
                            match downloadResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected SDK DownloadAttachment success but got error: {error.Error}")
                                Parameters.WorkItem.DownloadWorkItemAttachmentResult()

                        Assert.That(download.ArtifactId, Is.EqualTo(shownAttachment.ArtifactId))
                        Assert.That(download.AttachmentType, Is.EqualTo("summary"))
                        Assert.That(String.IsNullOrWhiteSpace(download.DownloadUri), Is.False)
                    })
        }

    /// Verifies the SDK work item link apis propagate validation not found and authorization errors scenario.
    [<Test>]
    member _.SdkWorkItemLinkApisPropagateValidationNotFoundAndAuthorizationErrors() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-sdk-errors"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "sdk errors"

            do!
                runWithSdkAuthentication (fun () ->
                    task {
                        let invalidParameters = Parameters.WorkItem.GetWorkItemLinksParameters()
                        invalidParameters.OwnerId <- ownerId
                        invalidParameters.OrganizationId <- organizationId
                        invalidParameters.RepositoryId <- repositoryId
                        invalidParameters.WorkItemId <- "0"
                        invalidParameters.CorrelationId <- generateCorrelationId ()

                        let! invalidResult = Grace.SDK.WorkItem.GetLinks invalidParameters

                        match invalidResult with
                        | Ok _ -> Assert.Fail("Expected validation error for non-positive WorkItemNumber.")
                        | Error error -> Assert.That(error.Error, Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber))

                        let notFoundParameters = Parameters.WorkItem.GetWorkItemLinksParameters()
                        notFoundParameters.OwnerId <- ownerId
                        notFoundParameters.OrganizationId <- organizationId
                        notFoundParameters.RepositoryId <- repositoryId
                        notFoundParameters.WorkItemId <- Int64.MaxValue.ToString()
                        notFoundParameters.CorrelationId <- generateCorrelationId ()

                        let! notFoundResult = Grace.SDK.WorkItem.GetLinks notFoundParameters

                        match notFoundResult with
                        | Ok _ -> Assert.Fail("Expected not-found error for unknown WorkItemNumber.")
                        | Error error -> Assert.That(error.Error, Does.Contain(WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist))
                    })

            let configuration = Current()
            configuration.ServerUri <- graceServerBaseAddress
            Grace.SDK.Auth.clearTokenProvider ()

            let unauthorizedParameters = Parameters.WorkItem.GetWorkItemLinksParameters()
            unauthorizedParameters.OwnerId <- ownerId
            unauthorizedParameters.OrganizationId <- organizationId
            unauthorizedParameters.RepositoryId <- repositoryId
            unauthorizedParameters.WorkItemId <- workItemId
            unauthorizedParameters.CorrelationId <- generateCorrelationId ()

            let! unauthorizedResult = Grace.SDK.WorkItem.GetLinks unauthorizedParameters

            match unauthorizedResult with
            | Ok _ -> Assert.Fail("Expected authorization error when SDK token provider is not configured.")
            | Error _ -> ()
        }
