namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open Grace.Types.Policy
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Threading.Tasks

module private PolicyServerTestHelpers =
    let createAuthenticatedClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    let getCurrentParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Policy.GetPolicyParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let acknowledgeParameters (repositoryId: string) (branchId: string) (snapshotId: string) (note: string) =
        let parameters = Parameters.Policy.AcknowledgePolicyParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.PolicySnapshotId <- snapshotId
        parameters.Note <- note
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let postAsync (client: HttpClient) (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        client.SendAsync(request)

    let assertBadRequestContainsAsync expectedText (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
        }

[<NonParallelizable>]
type PolicyApiIntegrationTests() =

    [<Test>]
    member _.CurrentWithoutSnapshotReturnsCoherentNoneShapeAndAcknowledgeRejectsMissingOrStaleSnapshot() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = $"{Guid.NewGuid()}"
            let userId = $"{Guid.NewGuid()}"

            use client = PolicyServerTestHelpers.createAuthenticatedClient userId

            let! currentResponse =
                PolicyServerTestHelpers.postAsync
                    client
                    "/policy/current"
                    (createJsonContent (PolicyServerTestHelpers.getCurrentParameters repositoryId branchId))

            let! currentBody = currentResponse.Content.ReadAsStringAsync()
            Assert.That(currentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), currentBody)

            let current = deserialize<GraceReturnValue<PolicySnapshot option>> currentBody
            Assert.That(current.ReturnValue.IsNone, Is.True)

            Assert.That(
                current
                    .Properties[ nameof RepositoryId ]
                    .ToString(),
                Is.EqualTo(repositoryId)
            )

            Assert.That(current.Properties[ nameof BranchId ].ToString(), Is.EqualTo(branchId))
            Assert.That(current.Properties[ "Path" ].ToString(), Is.EqualTo("/policy/current"))

            let! missingResponse =
                PolicyServerTestHelpers.postAsync
                    client
                    "/policy/acknowledge"
                    (createJsonContent (PolicyServerTestHelpers.acknowledgeParameters repositoryId branchId String.Empty "missing"))

            do! PolicyServerTestHelpers.assertBadRequestContainsAsync (PolicyError.getErrorMessage PolicyError.InvalidPolicySnapshotId) missingResponse

            let! staleResponse =
                PolicyServerTestHelpers.postAsync
                    client
                    "/policy/acknowledge"
                    (createJsonContent (PolicyServerTestHelpers.acknowledgeParameters repositoryId branchId $"stale-{Guid.NewGuid():N}" "stale"))

            do! PolicyServerTestHelpers.assertBadRequestContainsAsync (PolicyError.getErrorMessage PolicyError.PolicySnapshotDoesNotExist) staleResponse
        }
