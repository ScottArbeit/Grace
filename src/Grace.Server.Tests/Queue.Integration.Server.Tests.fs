namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open Grace.Types.PromotionSet
open Grace.Types.Queue
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

module private QueueIntegrationTestHelpers =
    let queueStatusParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Queue.QueueStatusParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let queueActionParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Queue.QueueActionParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let enqueueParameters (repositoryId: string) (branchId: string) (promotionSetId: string) (policySnapshotId: string) =
        let parameters = Parameters.Queue.EnqueueParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.PromotionSetId <- promotionSetId
        parameters.PolicySnapshotId <- policySnapshotId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let dequeueParameters (repositoryId: string) (branchId: string) (promotionSetId: string) =
        let parameters = Parameters.Queue.PromotionSetActionParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.PromotionSetId <- promotionSetId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

    let createPromotionSetAsync (repositoryId: string) (branchId: string) =
        task {
            let promotionSetId = $"{Guid.NewGuid()}"
            let parameters = Parameters.PromotionSet.CreatePromotionSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PromotionSetId <- promotionSetId
            parameters.TargetBranchId <- branchId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = postAsync "/promotion-set/create" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return promotionSetId
        }

    let getQueueAsync (repositoryId: string) (branchId: string) =
        task {
            let! response = postAsync "/queue/status" (createJsonContent (queueStatusParameters repositoryId branchId))
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            use document = JsonDocument.Parse(body)
            let root = document.RootElement
            let mutable returnValueElement = Unchecked.defaultof<JsonElement>

            let queueJson =
                if root.TryGetProperty("ReturnValue", &returnValueElement) then
                    returnValueElement.GetRawText()
                elif root.TryGetProperty("returnValue", &returnValueElement) then
                    returnValueElement.GetRawText()
                else
                    body

            try
                return deserialize<PromotionQueue> queueJson
            with
            | ex ->
                Assert.Fail($"Expected queue status JSON to deserialize as PromotionQueue. Body: {body}. Error: {ex.Message}")
                return PromotionQueue.Default
        }

    let postOkAsync (route: string) (content: HttpContent) =
        task {
            let! response = postAsync route content
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
        }

    let assertBadRequestContainsAsync expectedText (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
        }

[<NonParallelizable>]
type QueueApiIntegrationTests() =

    [<Test>]
    member _.QueueStatusEnqueuePauseResumeAndDequeueFollowHostedLifecycle() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let policySnapshotId = $"policy-{Guid.NewGuid():N}"
            let! promotionSetId = QueueIntegrationTestHelpers.createPromotionSetAsync repositoryId branchId

            do!
                QueueIntegrationTestHelpers.postOkAsync
                    "/queue/enqueue"
                    (createJsonContent (QueueIntegrationTestHelpers.enqueueParameters repositoryId branchId promotionSetId policySnapshotId))

            let! status = QueueIntegrationTestHelpers.getQueueAsync repositoryId branchId
            Assert.That(status.State, Is.EqualTo(QueueState.Idle))
            Assert.That(status.PromotionSetIds, Is.Empty)

            do!
                QueueIntegrationTestHelpers.postOkAsync
                    "/queue/pause"
                    (createJsonContent (QueueIntegrationTestHelpers.queueActionParameters repositoryId branchId))

            do!
                QueueIntegrationTestHelpers.postOkAsync
                    "/queue/resume"
                    (createJsonContent (QueueIntegrationTestHelpers.queueActionParameters repositoryId branchId))

            do!
                QueueIntegrationTestHelpers.postOkAsync
                    "/queue/dequeue"
                    (createJsonContent (QueueIntegrationTestHelpers.dequeueParameters repositoryId branchId promotionSetId))

            let! dequeued = QueueIntegrationTestHelpers.getQueueAsync repositoryId branchId
            Assert.That(dequeued.State, Is.EqualTo(QueueState.Idle))
            Assert.That(dequeued.PromotionSetIds, Is.Empty)
        }

    [<Test>]
    member _.QueueEnqueueRejectsMissingSnapshotForInitializationAndInvalidPromotionSetInput() =
        task {
            let repositoryId = repositoryIds[0]
            let missingSnapshotBranchId = $"{Guid.NewGuid()}"
            let invalidPromotionBranchId = $"{Guid.NewGuid()}"
            let! promotionSetId = QueueIntegrationTestHelpers.createPromotionSetAsync repositoryId missingSnapshotBranchId

            let! missingSnapshotResponse =
                QueueIntegrationTestHelpers.postAsync
                    "/queue/enqueue"
                    (createJsonContent (QueueIntegrationTestHelpers.enqueueParameters repositoryId missingSnapshotBranchId promotionSetId String.Empty))

            do! QueueIntegrationTestHelpers.assertBadRequestContainsAsync "PolicySnapshotId is required to initialize the queue." missingSnapshotResponse

            let! invalidPromotionResponse =
                QueueIntegrationTestHelpers.postAsync
                    "/queue/enqueue"
                    (createJsonContent (
                        QueueIntegrationTestHelpers.enqueueParameters repositoryId invalidPromotionBranchId "not-a-promotion-set" $"policy-{Guid.NewGuid():N}"
                    ))

            do! QueueIntegrationTestHelpers.assertBadRequestContainsAsync (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) invalidPromotionResponse

            let! stalePromotionResponse =
                QueueIntegrationTestHelpers.postAsync
                    "/queue/enqueue"
                    (createJsonContent (
                        QueueIntegrationTestHelpers.enqueueParameters repositoryId invalidPromotionBranchId $"{Guid.NewGuid()}" $"policy-{Guid.NewGuid():N}"
                    ))

            do! QueueIntegrationTestHelpers.assertBadRequestContainsAsync "The specified promotion set does not exist." stalePromotionResponse
        }
