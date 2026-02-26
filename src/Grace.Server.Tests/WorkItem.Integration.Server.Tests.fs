namespace Grace.Server.Tests

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
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"{repositoryNamePrefix}-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
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
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<WorkItemLinksDto>> response
            return returnValue.ReturnValue
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

            Grace.SDK.Auth.setTokenProvider(fun () ->
                task {
                    return Some token
                })
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
            let! response =
                WorkItemIntegrationHelpers.getWorkItemResponseAsync Client repositoryId (Int64.MaxValue.ToString())

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response

            Assert.That(
                error.Error,
                Does.Contain(WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist)
            )
        }

    [<Test>]
    member _.NonPositiveNumericIdentifierReturnsValidationError() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-invalid-number"
            let! response = WorkItemIntegrationHelpers.getWorkItemResponseAsync Client repositoryId "0"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response

            Assert.That(
                error.Error,
                Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber)
            )
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
                    for index in 0 .. createCount - 1 ->
                        WorkItemIntegrationHelpers.createWorkItemAsync repositoryId $"concurrent {index}"
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
            removedReference.EnsureSuccessStatusCode() |> ignore

            let! removedPromotionSet = WorkItemIntegrationHelpers.removePromotionSetLinkAsync Client repositoryId workItemId promotionSetId
            removedPromotionSet.EnsureSuccessStatusCode() |> ignore

            let! removedArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync Client repositoryId workItemId summaryArtifactId
            removedArtifact.EnsureSuccessStatusCode() |> ignore

            let! removedSummary = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync Client repositoryId workItemId "summary"
            removedSummary.EnsureSuccessStatusCode() |> ignore

            let! removedPrompt = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync Client repositoryId workItemId "prompt"
            removedPrompt.EnsureSuccessStatusCode() |> ignore

            let! removedNotes = WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync Client repositoryId workItemId "notes"
            removedNotes.EnsureSuccessStatusCode() |> ignore

            let! removedPromptArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync Client repositoryId workItemId promptArtifactId
            removedPromptArtifact.EnsureSuccessStatusCode() |> ignore

            let! removedNotesArtifact = WorkItemIntegrationHelpers.removeArtifactLinkAsync Client repositoryId workItemId notesArtifactId
            removedNotesArtifact.EnsureSuccessStatusCode() |> ignore

            let! afterRemoval = WorkItemIntegrationHelpers.getWorkItemLinksAsync Client repositoryId workItemId

            Assert.That(afterRemoval.ReferenceIds, Is.Empty)
            Assert.That(afterRemoval.PromotionSetIds, Is.Empty)
            Assert.That(afterRemoval.AgentSummaryArtifactIds, Is.Empty)
            Assert.That(afterRemoval.PromptArtifactIds, Is.Empty)
            Assert.That(afterRemoval.ReviewNotesArtifactIds, Is.Empty)
            Assert.That(afterRemoval.OtherArtifactIds, Is.Empty)
            Assert.That(afterRemoval.ArtifactIds, Is.Empty)
        }

[<Parallelizable(ParallelScope.All)>]
type WorkItemLinksAuthorizationIntegrationTests() =

    [<Test>]
    member _.WorkItemLinkEndpointsRequireAuthenticationAndAllowAuthenticatedUsers() =
        task {
            let! repositoryId = WorkItemIntegrationHelpers.createRepositoryAsync "wi-links-auth"
            let! workItemId = WorkItemIntegrationHelpers.createWorkItemAsync repositoryId "auth matrix"
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
                    "/work/links/remove/reference",
                    (fun () ->
                        WorkItemIntegrationHelpers.removeReferenceLinkAsync
                            unauthenticatedClient
                            repositoryId
                            workItemId
                            (Guid.NewGuid()))
                    "/work/links/remove/promotion-set",
                    (fun () ->
                        WorkItemIntegrationHelpers.removePromotionSetLinkAsync
                            unauthenticatedClient
                            repositoryId
                            workItemId
                            (Guid.NewGuid()))
                    "/work/links/remove/artifact",
                    (fun () ->
                        WorkItemIntegrationHelpers.removeArtifactLinkAsync
                            unauthenticatedClient
                            repositoryId
                            workItemId
                            (Guid.NewGuid()))
                    "/work/links/remove/artifact-type",
                    (fun () ->
                        WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync
                            unauthenticatedClient
                            repositoryId
                            workItemId
                            "summary")
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

            let! removeReferenceAuthenticated =
                WorkItemIntegrationHelpers.removeReferenceLinkAsync authenticatedClient repositoryId workItemId (Guid.NewGuid())

            Assert.That(removeReferenceAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! removePromotionAuthenticated =
                WorkItemIntegrationHelpers.removePromotionSetLinkAsync authenticatedClient repositoryId workItemId (Guid.NewGuid())

            Assert.That(removePromotionAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! removeArtifactAuthenticated =
                WorkItemIntegrationHelpers.removeArtifactLinkAsync authenticatedClient repositoryId workItemId (Guid.NewGuid())

            Assert.That(removeArtifactAuthenticated.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! removeArtifactTypeAuthenticated =
                WorkItemIntegrationHelpers.removeArtifactTypeLinksAsync authenticatedClient repositoryId workItemId "summary"

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
                            | Error error -> Assert.Fail($"Expected SDK GetLinks success but got error: {error.Error}"); WorkItemLinksDto.Default

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
                            | Error error -> Assert.Fail($"Expected SDK GetLinks success but got error after removals: {error.Error}"); WorkItemLinksDto.Default

                        Assert.That(afterRemoval.ReferenceIds, Is.Empty)
                        Assert.That(afterRemoval.PromotionSetIds, Is.Empty)
                        Assert.That(afterRemoval.AgentSummaryArtifactIds, Is.Empty)
                        Assert.That(afterRemoval.PromptArtifactIds, Is.Empty)
                        Assert.That(afterRemoval.ReviewNotesArtifactIds, Is.Empty)
                        Assert.That(afterRemoval.OtherArtifactIds, Is.Empty)
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
                        | Error error ->
                            Assert.That(
                                error.Error,
                                Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber)
                            )

                        let notFoundParameters = Parameters.WorkItem.GetWorkItemLinksParameters()
                        notFoundParameters.OwnerId <- ownerId
                        notFoundParameters.OrganizationId <- organizationId
                        notFoundParameters.RepositoryId <- repositoryId
                        notFoundParameters.WorkItemId <- Int64.MaxValue.ToString()
                        notFoundParameters.CorrelationId <- generateCorrelationId ()

                        let! notFoundResult = Grace.SDK.WorkItem.GetLinks notFoundParameters

                        match notFoundResult with
                        | Ok _ -> Assert.Fail("Expected not-found error for unknown WorkItemNumber.")
                        | Error error ->
                            Assert.That(
                                error.Error,
                                Does.Contain(WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist)
                            )
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
