namespace Grace.Server.Tests

open Azure.Storage.Blobs
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Artifact
open Grace.Types.PersonalAccessToken
open Grace.Types.Types
open Grace.Types.WorkItem
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

module private WorkItemIntegrationHelpers =
    let createAuthenticatedClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

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

    let createWorkItemAsync (repositoryId: string) (title: string) =
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

            let! response = Client.PostAsync("/work/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            return workItemId
        }

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

    let getWorkItemDtoAsync (client: HttpClient) (repositoryId: string) (workItemIdentifier: string) =
        task {
            let! response = getWorkItemResponseAsync client repositoryId workItemIdentifier
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<WorkItemDto>> response
            return returnValue.ReturnValue
        }

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

    let createArtifactAsync (repositoryId: string) (artifactType: string) =
        task {
            let parameters = Parameters.Artifact.CreateArtifactParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ArtifactType <- artifactType
            parameters.MimeType <- "text/plain"
            parameters.Size <- 16L
            parameters.Sha256 <- ""
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/artifact/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<ArtifactCreateResult>> response
            return returnValue.ReturnValue.ArtifactId
        }

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

    let createPersonalAccessTokenAsync () =
        task {
            let parameters = Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"workitem-sdk-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/auth/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            return returnValue.ReturnValue.Token
        }

    let configureSdkForServerAsync () =
        task {
            let configuration = Current()
            configuration.ServerUri <- graceServerBaseAddress

            let! token = createPersonalAccessTokenAsync ()

            Grace.SDK.Auth.setTokenProvider (fun () -> task { return Some token })
        }

[<NonParallelizable>]
type WorkItemNumberAndLinksIntegrationTests() =

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

    [<Test>]
    member _.UnknownNumericIdentifierReturnsNotFoundError() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-unknown-number"
            let! response = WorkItemIntegrationHelpers.getWorkItemResponseAsync Client repositoryId (Int64.MaxValue.ToString())

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response

            Assert.That(error.Error, Does.Contain(WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist))
        }

    [<Test>]
    member _.NonPositiveNumericIdentifierReturnsValidationError() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-invalid-number"
            let! response = WorkItemIntegrationHelpers.getWorkItemResponseAsync Client repositoryId "0"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response

            Assert.That(error.Error, Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber))
        }

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

[<NonParallelizable>]
type WorkItemAddSummaryIntegrationTests() =

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

[<NonParallelizable>]
type WorkItemAttachmentEndpointsIntegrationTests() =

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

    [<Test>]
    member _.AttachmentDownloadReturnsDownloadUriForLinkedReviewerAttachmentAndRejectsInvalidArtifacts() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-attachments-download"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "attachments download"

            let! addSummaryResult =
                WorkItemIntegrationHelpers.addSummaryAsync Client repositoryId workItemId "download summary content" None None (generateCorrelationId ())

            let! linkedOtherArtifactId = WorkItemIntegrationHelpers.createArtifactAsync repositoryId "Other"
            do! WorkItemIntegrationHelpers.linkArtifactAsync repositoryId workItemId linkedOtherArtifactId

            let summaryArtifactId = Guid.Parse(addSummaryResult.SummaryArtifactId)
            let! downloadResult = WorkItemIntegrationHelpers.downloadWorkItemAttachmentAsync Client repositoryId workItemId summaryArtifactId

            Assert.That(downloadResult.ArtifactId, Is.EqualTo(summaryArtifactId.ToString()))
            Assert.That(downloadResult.AttachmentType, Is.EqualTo("summary"))
            Assert.That(String.IsNullOrWhiteSpace(downloadResult.DownloadUri), Is.False)

            let! notLinkedResponse = WorkItemIntegrationHelpers.downloadWorkItemAttachmentResponseAsync Client repositoryId workItemId (Guid.NewGuid())

            Assert.That(notLinkedResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! notLinkedError = deserializeContent<GraceError> notLinkedResponse
            Assert.That(notLinkedError.Error, Does.Contain("not linked"))
        }

[<Parallelizable(ParallelScope.All)>]
type WorkItemLinksAuthorizationIntegrationTests() =

    [<Test>]
    member _.WorkItemLinkEndpointsRequireAuthenticationAndAllowAuthenticatedUsers() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-links-auth"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "auth matrix"

            let summaryArtifactId = Guid.NewGuid()
            use unauthenticatedClient = WorkItemIntegrationHelpers.createUnauthenticatedClient ()
            use authenticatedClient = WorkItemIntegrationHelpers.createAuthenticatedClient $"{Guid.NewGuid()}"

            let calls: (string * (unit -> Task<HttpResponseMessage>)) list =
                [
                    "/work/links/list",
                    (fun () ->
                        task {
                            let parameters = Parameters.WorkItem.GetWorkItemLinksParameters()
                            parameters.OwnerId <- ownerId
                            parameters.OrganizationId <- organizationId
                            parameters.RepositoryId <- repositoryId
                            parameters.WorkItemId <- workItemId
                            parameters.CorrelationId <- generateCorrelationId ()
                            return! unauthenticatedClient.PostAsync("/work/links/list", createJsonContent parameters)
                        })
                    "/work/attachments/list",
                    (fun () ->
                        task {
                            let parameters = Parameters.WorkItem.ListWorkItemAttachmentsParameters()
                            parameters.OwnerId <- ownerId
                            parameters.OrganizationId <- organizationId
                            parameters.RepositoryId <- repositoryId
                            parameters.WorkItemId <- workItemId
                            parameters.CorrelationId <- generateCorrelationId ()
                            return! unauthenticatedClient.PostAsync("/work/attachments/list", createJsonContent parameters)
                        })
                    "/work/attachments/show",
                    (fun () ->
                        task {
                            let parameters = Parameters.WorkItem.ShowWorkItemAttachmentParameters()
                            parameters.OwnerId <- ownerId
                            parameters.OrganizationId <- organizationId
                            parameters.RepositoryId <- repositoryId
                            parameters.WorkItemId <- workItemId
                            parameters.AttachmentType <- "summary"
                            parameters.Latest <- true
                            parameters.CorrelationId <- generateCorrelationId ()
                            return! unauthenticatedClient.PostAsync("/work/attachments/show", createJsonContent parameters)
                        })
                    "/work/attachments/download",
                    (fun () ->
                        task {
                            let parameters = Parameters.WorkItem.DownloadWorkItemAttachmentParameters()
                            parameters.OwnerId <- ownerId
                            parameters.OrganizationId <- organizationId
                            parameters.RepositoryId <- repositoryId
                            parameters.WorkItemId <- workItemId
                            parameters.ArtifactId <- summaryArtifactId.ToString()
                            parameters.CorrelationId <- generateCorrelationId ()
                            return! unauthenticatedClient.PostAsync("/work/attachments/download", createJsonContent parameters)
                        })
                    "/work/links/remove/reference",
                    (fun () -> WorkItemIntegrationHelpers.removeReferenceLinkAsync unauthenticatedClient repositoryId workItemId (Guid.NewGuid()))
                    "/work/links/remove/promotion-set",
                    (fun () -> WorkItemIntegrationHelpers.removePromotionSetLinkAsync unauthenticatedClient repositoryId workItemId (Guid.NewGuid()))
                    "/work/links/remove/artifact",
                    (fun () -> WorkItemIntegrationHelpers.removeArtifactLinkAsync unauthenticatedClient repositoryId workItemId (Guid.NewGuid()))
                    "/work/links/remove/artifact-type",
                    (fun () -> WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync unauthenticatedClient repositoryId workItemId "summary")
                ]

            for (path, invokeUnauthenticated) in calls do
                let! response = invokeUnauthenticated ()
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"Expected 401 for {path}.")

            let! listAuthenticated =
                task {
                    let parameters = Parameters.WorkItem.GetWorkItemLinksParameters()
                    parameters.OwnerId <- ownerId
                    parameters.OrganizationId <- organizationId
                    parameters.RepositoryId <- repositoryId
                    parameters.WorkItemId <- workItemId
                    parameters.CorrelationId <- generateCorrelationId ()
                    return! authenticatedClient.PostAsync("/work/links/list", createJsonContent parameters)
                }

            Assert.That(listAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! listAttachmentsAuthenticated = WorkItemIntegrationHelpers.listWorkItemAttachmentsResponseAsync authenticatedClient repositoryId workItemId

            Assert.That(listAttachmentsAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! showAttachmentAuthenticated =
                WorkItemIntegrationHelpers.showWorkItemAttachmentResponseAsync authenticatedClient repositoryId workItemId "summary" true

            Assert.That(showAttachmentAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let! downloadAttachmentAuthenticated =
                WorkItemIntegrationHelpers.downloadWorkItemAttachmentResponseAsync authenticatedClient repositoryId workItemId summaryArtifactId

            Assert.That(downloadAttachmentAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let! removeReferenceAuthenticated = WorkItemIntegrationHelpers.removeReferenceLinkAsync authenticatedClient repositoryId workItemId (Guid.NewGuid())

            Assert.That(removeReferenceAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! removePromotionAuthenticated =
                WorkItemIntegrationHelpers.removePromotionSetLinkAsync authenticatedClient repositoryId workItemId (Guid.NewGuid())

            Assert.That(removePromotionAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! removeArtifactAuthenticated = WorkItemIntegrationHelpers.removeArtifactLinkAsync authenticatedClient repositoryId workItemId (Guid.NewGuid())

            Assert.That(removeArtifactAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! removeArtifactTypeAuthenticated = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync authenticatedClient repositoryId workItemId "summary"

            Assert.That(removeArtifactTypeAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

[<NonParallelizable>]
type WorkItemSdkSmokeIntegrationTests() =

    let runWithSdkAuthentication (testBody: unit -> Task<unit>) =
        task {
            do! WorkItemIntegrationHelpers.configureSdkForServerAsync ()

            try
                do! testBody ()
            finally
                Grace.SDK.Auth.clearTokenProvider ()
        }

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
