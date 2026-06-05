namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types
open Grace.Types.Common
open Grace.Types.Policy
open Grace.Types.PromotionSet
open Grace.Types.Queue
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

module private QueueIntegrationTestHelpers =
    let getBranchParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Branch.GetBranchParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.BranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getPolicyParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Policy.GetPolicyParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

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

    let seedPolicySnapshotParameters (repositoryId: string) (branchId: string) (policySnapshotId: string) =
        let parameters = Grace.Server.Policy.SeedPolicySnapshotParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.PolicySnapshotId <- policySnapshotId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

    let getJsonProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            property
        elif element.TryGetProperty($"{Char.ToLowerInvariant(name[0])}{name.Substring(1)}", &property) then
            property
        else
            Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}.")
            Unchecked.defaultof<JsonElement>

    let waitForBranchAsync (repositoryId: string) (branchId: string) =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(15.0)
            let mutable foundBranch: Branch.BranchDto option = Option.None
            let mutable lastBody = String.Empty
            let mutable lastStatus = HttpStatusCode.OK

            while foundBranch.IsNone && DateTime.UtcNow < timeoutAt do
                let! response = postAsync "/branch/get" (createJsonContent (getBranchParameters repositoryId branchId))
                let! body = response.Content.ReadAsStringAsync()
                lastBody <- body
                lastStatus <- response.StatusCode

                if response.StatusCode = HttpStatusCode.OK then
                    let returnValue = deserialize<GraceReturnValue<Branch.BranchDto>> body
                    foundBranch <- Some returnValue.ReturnValue
                else
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            if foundBranch.IsSome then
                return foundBranch.Value
            else
                Assert.Fail($"Timed out waiting for branch {branchId} in repository {repositoryId}. Last status: {lastStatus}; body: {lastBody}")
                return Unchecked.defaultof<Branch.BranchDto>
        }

    let createIsolatedBranchAsync (repositoryId: string) =
        task {
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = waitForBranchAsync repositoryId parentBranchId
            let branchId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Branch.CreateBranchParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.BranchName <- $"queue-proof-{Guid.NewGuid():N}"
            parameters.ParentBranchId <- $"{parentBranch.BranchId}"
            parameters.ParentBranchName <- $"{parentBranch.BranchName}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = postAsync "/branch/create" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return! waitForBranchAsync repositoryId branchId
        }

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

    let seedPolicySnapshotAsync (repositoryId: string) (branchId: string) =
        task {
            let policySnapshotId = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"
            let! response = postAsync "/policy/_seedSnapshot" (createJsonContent (seedPolicySnapshotParameters repositoryId branchId policySnapshotId))
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            Assert.That(body, Does.Contain(policySnapshotId))

            return policySnapshotId
        }

    let getQueueWithBodyAsync (repositoryId: string) (branchId: string) =
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
                return deserialize<PromotionQueue> queueJson, body
            with
            | ex ->
                Assert.Fail($"Expected queue status JSON to deserialize as PromotionQueue. Body: {body}. Error: {ex.Message}")
                return PromotionQueue.Default, body
        }

    let getQueueAsync repositoryId branchId =
        task {
            let! queue, _body = getQueueWithBodyAsync repositoryId branchId
            return queue
        }

    let postOkWithBodyAsync (route: string) (content: HttpContent) =
        task {
            let! response = postAsync route content
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let postOkAsync route content =
        task {
            let! _body = postOkWithBodyAsync route content
            return ()
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
            let! branch = QueueIntegrationTestHelpers.createIsolatedBranchAsync repositoryId
            let branchId = $"{branch.BranchId}"
            let! policySnapshotId = QueueIntegrationTestHelpers.seedPolicySnapshotAsync repositoryId branchId
            let! promotionSetId = QueueIntegrationTestHelpers.createPromotionSetAsync repositoryId branchId

            let! enqueueBody =
                QueueIntegrationTestHelpers.postOkWithBodyAsync
                    "/queue/enqueue"
                    (createJsonContent (QueueIntegrationTestHelpers.enqueueParameters repositoryId branchId promotionSetId policySnapshotId))

            let! enqueued, enqueuedBody = QueueIntegrationTestHelpers.getQueueWithBodyAsync repositoryId branchId
            let lifecycleMessage = $"Enqueue body: {enqueueBody}{Environment.NewLine}Status body: {enqueuedBody}"
            Assert.That(enqueued.TargetBranchId, Is.EqualTo(branch.BranchId), lifecycleMessage)
            Assert.That(enqueued.PolicySnapshotId, Is.EqualTo(PolicySnapshotId policySnapshotId), lifecycleMessage)
            Assert.That(enqueued.State, Is.EqualTo(QueueState.Idle), lifecycleMessage)
            Assert.That(enqueued.PromotionSetIds, Does.Contain(Guid.Parse promotionSetId), lifecycleMessage)

            do!
                QueueIntegrationTestHelpers.postOkAsync
                    "/queue/pause"
                    (createJsonContent (QueueIntegrationTestHelpers.queueActionParameters repositoryId branchId))

            let! paused = QueueIntegrationTestHelpers.getQueueAsync repositoryId branchId
            Assert.That(paused.State, Is.EqualTo(QueueState.Paused))
            Assert.That(paused.PromotionSetIds, Does.Contain(Guid.Parse promotionSetId))

            do!
                QueueIntegrationTestHelpers.postOkAsync
                    "/queue/resume"
                    (createJsonContent (QueueIntegrationTestHelpers.queueActionParameters repositoryId branchId))

            let! resumed = QueueIntegrationTestHelpers.getQueueAsync repositoryId branchId
            Assert.That(resumed.State, Is.EqualTo(QueueState.Idle))
            Assert.That(resumed.PromotionSetIds, Does.Contain(Guid.Parse promotionSetId))

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
            let! missingSnapshotBranch = QueueIntegrationTestHelpers.createIsolatedBranchAsync repositoryId
            let missingSnapshotBranchId = $"{missingSnapshotBranch.BranchId}"
            let! staleSnapshotBranch = QueueIntegrationTestHelpers.createIsolatedBranchAsync repositoryId
            let staleSnapshotBranchId = $"{staleSnapshotBranch.BranchId}"
            let invalidPromotionBranchId = $"{Guid.NewGuid()}"
            let! promotionSetId = QueueIntegrationTestHelpers.createPromotionSetAsync repositoryId missingSnapshotBranchId

            let! missingSnapshotResponse =
                QueueIntegrationTestHelpers.postAsync
                    "/queue/enqueue"
                    (createJsonContent (QueueIntegrationTestHelpers.enqueueParameters repositoryId missingSnapshotBranchId promotionSetId String.Empty))

            do! QueueIntegrationTestHelpers.assertBadRequestContainsAsync "PolicySnapshotId is required to initialize the queue." missingSnapshotResponse

            let! noCurrentPolicyPromotionSetId = QueueIntegrationTestHelpers.createPromotionSetAsync repositoryId staleSnapshotBranchId

            let! noCurrentPolicyResponse =
                QueueIntegrationTestHelpers.postAsync
                    "/queue/enqueue"
                    (createJsonContent (
                        QueueIntegrationTestHelpers.enqueueParameters
                            repositoryId
                            staleSnapshotBranchId
                            noCurrentPolicyPromotionSetId
                            $"policy-{Guid.NewGuid():N}"
                    ))

            do! QueueIntegrationTestHelpers.assertBadRequestContainsAsync "Queue initialization requires a current policy snapshot." noCurrentPolicyResponse

            let! realPolicySnapshotId = QueueIntegrationTestHelpers.seedPolicySnapshotAsync repositoryId staleSnapshotBranchId

            let! staleSnapshotResponse =
                QueueIntegrationTestHelpers.postAsync
                    "/queue/enqueue"
                    (createJsonContent (
                        QueueIntegrationTestHelpers.enqueueParameters
                            repositoryId
                            staleSnapshotBranchId
                            noCurrentPolicyPromotionSetId
                            $"stale-{Guid.NewGuid():N}"
                    ))

            do! QueueIntegrationTestHelpers.assertBadRequestContainsAsync "PolicySnapshotId does not match the current policy snapshot." staleSnapshotResponse
            Assert.That(realPolicySnapshotId, Is.Not.Empty)

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
