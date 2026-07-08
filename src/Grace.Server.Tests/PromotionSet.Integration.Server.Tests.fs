namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Parameters.PromotionSet
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.PromotionSet
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// Groups shared helpers for promotion set integration helpers.
module private PromotionSetIntegrationHelpers =
    /// Defines scoped behavior for the surrounding tests used by the server integration promotion Set Integration scenario.
    let private scoped<'T when 'T :> PromotionSetParameters> (parameters: 'T) repositoryId promotionSetId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.PromotionSetId <- promotionSetId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Posts Async to the running test server.
    let postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

    /// Posts ok to the running test server.
    let postOkAsync route parameters =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    /// Posts bad request contains to the running test server.
    let postBadRequestContainsAsync route parameters expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    /// Posts status contains to the running test server.
    let postStatusContainsAsync route parameters (expectedStatus: HttpStatusCode) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    /// Requires property and fails the test when missing.
    let private requireProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            property
        elif element.TryGetProperty($"{Char.ToLowerInvariant(name[0])}{name.Substring(1)}", &property) then
            property
        else
            Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}.")
            Unchecked.defaultof<JsonElement>

    /// Asserts event sequence for integration responses.
    let assertEventSequence (body: string) (promotionSetId: string) (expectedNames: string array) =
        use document = JsonDocument.Parse(body)

        let returnValue =
            document.RootElement
            |> requireProperty "ReturnValue"

        let eventNames =
            returnValue.EnumerateArray()
            |> Seq.map (fun eventEnvelope ->
                let eventElement = eventEnvelope |> requireProperty "Event"
                let eventProperty = eventElement.EnumerateObject() |> Seq.exactlyOne
                eventProperty.Name)
            |> Seq.toArray

        let actorIds =
            returnValue.EnumerateArray()
            |> Seq.map (fun eventEnvelope ->
                let metadata = eventEnvelope |> requireProperty "Metadata"
                let properties = metadata |> requireProperty "Properties"

                (properties |> requireProperty "ActorId")
                    .GetString())
            |> Seq.toArray

        Assert.That(eventNames, Is.EqualTo<string array>(expectedNames))
        Assert.That(actorIds |> Array.forall ((=) promotionSetId), Is.True)

    /// Asserts a PromotionSet DTO response serialized successfully with visibility metadata.
    let assertPromotionSetDtoPayload (body: string) =
        use document = JsonDocument.Parse(body)

        let returnValue =
            document.RootElement
            |> requireProperty "ReturnValue"

        let className =
            (returnValue |> requireProperty "Class")
                .GetString()

        Assert.That(className, Is.EqualTo("PromotionSetDto"))

        returnValue
        |> requireProperty "Visibility"
        |> ignore

        returnValue
        |> requireProperty "Ownership"
        |> ignore

        returnValue |> requireProperty "Status" |> ignore

    /// Gets promotion set from the running test server.
    let getPromotionSetAsync repositoryId promotionSetId =
        task {
            let parameters = scoped (GetPromotionSetParameters()) repositoryId promotionSetId
            let! response = postAsync "/promotion-set/get" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    /// Gets events from the running test server.
    let getEventsAsync repositoryId promotionSetId =
        task {
            let parameters = scoped (GetPromotionSetEventsParameters()) repositoryId promotionSetId
            let! response = postAsync "/promotion-set/get-events" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    /// Builds a deterministic async for integration setup fixture for the server integration promotion Set Integration assertions.
    let createAsync repositoryId targetBranchId =
        task {
            let promotionSetId = Guid.NewGuid().ToString()
            let parameters = scoped (CreatePromotionSetParameters()) repositoryId promotionSetId
            parameters.TargetBranchId <- targetBranchId
            let! _ = postOkAsync "/promotion-set/create" parameters
            return promotionSetId
        }

    /// Defines update input promotions behavior for the surrounding tests used by the server integration promotion Set Integration scenario.
    let updateInputPromotionsAsync repositoryId promotionSetId promotionPointers =
        task {
            let parameters = scoped (UpdatePromotionSetInputPromotionsParameters()) repositoryId promotionSetId
            parameters.PromotionPointers <- promotionPointers

            let! _ =
                postBadRequestContainsAsync
                    "/promotion-set/update-input-promotions"
                    parameters
                    "DirectoryVersion was not found while recomputing PromotionSet step."

            return ()
        }

    /// Defines recompute behavior for the surrounding tests used by the server integration promotion Set Integration scenario.
    let recomputeAsync repositoryId promotionSetId reason =
        let parameters = scoped (RecomputePromotionSetParameters()) repositoryId promotionSetId
        parameters.Reason <- reason
        postOkAsync "/promotion-set/recompute" parameters

    /// Defines apply bad request behavior for the surrounding tests used by the server integration promotion Set Integration scenario.
    let applyBadRequestAsync repositoryId promotionSetId expectedText =
        let parameters = scoped (ApplyPromotionSetParameters()) repositoryId promotionSetId
        postBadRequestContainsAsync "/promotion-set/apply" parameters expectedText

    /// Defines resolve conflicts current failure behavior for the surrounding tests used by the server integration promotion Set Integration scenario.
    let resolveConflictsCurrentFailureAsync repositoryId promotionSetId stepId =
        let parameters = scoped (ResolvePromotionSetConflictsParameters()) repositoryId promotionSetId
        parameters.StepId <- stepId
        parameters.StepsComputationAttempt <- 1

        parameters.Decisions <-
            [
                { FilePath = "src/app.fs"; Accepted = true; OverrideContentArtifactId = Option.None }
            ]

        postStatusContainsAsync $"/promotion-set/{promotionSetId}/resolve-conflicts" parameters HttpStatusCode.InternalServerError "ValidateIdsMiddleware"

    /// Defines delete behavior for the surrounding tests used by the server integration promotion Set Integration scenario.
    let deleteAsync repositoryId promotionSetId =
        task {
            let parameters = scoped (DeletePromotionSetParameters()) repositoryId promotionSetId
            parameters.Force <- true
            parameters.DeleteReason <- "integration lifecycle cleanup"
            let! _ = postOkAsync "/promotion-set/delete" parameters
            return ()
        }

/// Covers promotion set route scenarios.
[<NonParallelizable>]
type PromotionSetRouteIntegrationTests() =

    /// Verifies the promotion set lifecycle preserves state events projection order and guarded mutations scenario.
    [<Test>]
    member _.PromotionSetLifecyclePreservesStateEventsProjectionOrderAndGuardedMutations() =
        task {
            let repositoryId = repositoryIds[0]
            let targetBranchId = repositoryDefaultBranchIds[0]
            let! promotionSetId = PromotionSetIntegrationHelpers.createAsync repositoryId targetBranchId

            let! created = PromotionSetIntegrationHelpers.getPromotionSetAsync repositoryId promotionSetId
            PromotionSetIntegrationHelpers.assertPromotionSetDtoPayload created

            let! recomputeBody = PromotionSetIntegrationHelpers.recomputeAsync repositoryId promotionSetId "hosted proof empty set"

            Assert.That(
                recomputeBody,
                Does
                    .Contain("PromotionSet steps are already computed")
                    .Or.Contain("PromotionSet command processed")
                    .Or.Contain("Promotion set command succeeded")
            )

            let firstPointer = { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }

            let secondPointer = { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }

            do! PromotionSetIntegrationHelpers.updateInputPromotionsAsync repositoryId promotionSetId [ firstPointer; secondPointer ]

            let! updated = PromotionSetIntegrationHelpers.getPromotionSetAsync repositoryId promotionSetId
            PromotionSetIntegrationHelpers.assertPromotionSetDtoPayload updated

            let! eventsBeforeDelete = PromotionSetIntegrationHelpers.getEventsAsync repositoryId promotionSetId

            PromotionSetIntegrationHelpers.assertEventSequence
                eventsBeforeDelete
                promotionSetId
                [|
                    "created"
                    "recomputeStarted"
                    "stepsUpdated"
                    "inputPromotionsUpdated"
                    "recomputeStarted"
                    "recomputeFailed"
                |]

            let! _ = PromotionSetIntegrationHelpers.resolveConflictsCurrentFailureAsync repositoryId promotionSetId (Guid.NewGuid().ToString())

            let! _ = PromotionSetIntegrationHelpers.applyBadRequestAsync repositoryId promotionSetId "DirectoryVersion was not found"

            do! PromotionSetIntegrationHelpers.deleteAsync repositoryId promotionSetId
            let! deleted = PromotionSetIntegrationHelpers.getPromotionSetAsync repositoryId promotionSetId
            PromotionSetIntegrationHelpers.assertPromotionSetDtoPayload deleted

            let! eventsAfterDelete = PromotionSetIntegrationHelpers.getEventsAsync repositoryId promotionSetId

            PromotionSetIntegrationHelpers.assertEventSequence
                eventsAfterDelete
                promotionSetId
                [|
                    "created"
                    "recomputeStarted"
                    "stepsUpdated"
                    "inputPromotionsUpdated"
                    "recomputeStarted"
                    "recomputeFailed"
                    "recomputeStarted"
                    "recomputeFailed"
                    "logicalDeleted"
                |]
        }
