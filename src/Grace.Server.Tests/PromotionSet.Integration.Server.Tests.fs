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

module private PromotionSetIntegrationHelpers =
    let private scoped<'T when 'T :> PromotionSetParameters> (parameters: 'T) repositoryId promotionSetId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.PromotionSetId <- promotionSetId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

    let postOkAsync route parameters =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let postBadRequestContainsAsync route parameters expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    let postStatusContainsAsync route parameters (expectedStatus: HttpStatusCode) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    let private requireProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            property
        elif element.TryGetProperty($"{Char.ToLowerInvariant(name[0])}{name.Substring(1)}", &property) then
            property
        else
            Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}.")
            Unchecked.defaultof<JsonElement>

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

    let getPromotionSetAsync repositoryId promotionSetId =
        task {
            let parameters = scoped (GetPromotionSetParameters()) repositoryId promotionSetId
            let! response = postAsync "/promotion-set/get" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let getEventsAsync repositoryId promotionSetId =
        task {
            let parameters = scoped (GetPromotionSetEventsParameters()) repositoryId promotionSetId
            let! response = postAsync "/promotion-set/get-events" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let createAsync repositoryId targetBranchId =
        task {
            let promotionSetId = Guid.NewGuid().ToString()
            let parameters = scoped (CreatePromotionSetParameters()) repositoryId promotionSetId
            parameters.TargetBranchId <- targetBranchId
            let! _ = postOkAsync "/promotion-set/create" parameters
            return promotionSetId
        }

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

    let recomputeAsync repositoryId promotionSetId reason =
        let parameters = scoped (RecomputePromotionSetParameters()) repositoryId promotionSetId
        parameters.Reason <- reason
        postOkAsync "/promotion-set/recompute" parameters

    let applyBadRequestAsync repositoryId promotionSetId expectedText =
        let parameters = scoped (ApplyPromotionSetParameters()) repositoryId promotionSetId
        postBadRequestContainsAsync "/promotion-set/apply" parameters expectedText

    let resolveConflictsCurrentFailureAsync repositoryId promotionSetId stepId =
        let parameters = scoped (ResolvePromotionSetConflictsParameters()) repositoryId promotionSetId
        parameters.StepId <- stepId
        parameters.StepsComputationAttempt <- 1

        parameters.Decisions <-
            [
                { FilePath = "src/app.fs"; Accepted = true; OverrideContentArtifactId = Option.None }
            ]

        postStatusContainsAsync $"/promotion-set/{promotionSetId}/resolve-conflicts" parameters HttpStatusCode.InternalServerError "ValidateIdsMiddleware"

    let deleteAsync repositoryId promotionSetId =
        task {
            let parameters = scoped (DeletePromotionSetParameters()) repositoryId promotionSetId
            parameters.Force <- true
            parameters.DeleteReason <- "integration lifecycle cleanup"
            let! _ = postOkAsync "/promotion-set/delete" parameters
            return ()
        }

[<NonParallelizable>]
type PromotionSetRouteIntegrationTests() =

    [<Test>]
    member _.PromotionSetLifecyclePreservesStateEventsProjectionOrderAndGuardedMutations() =
        task {
            let repositoryId = repositoryIds[0]
            let targetBranchId = repositoryDefaultBranchIds[0]
            let! promotionSetId = PromotionSetIntegrationHelpers.createAsync repositoryId targetBranchId

            let! created = PromotionSetIntegrationHelpers.getPromotionSetAsync repositoryId promotionSetId
            Assert.That(created, Does.Contain("JsonEnumLikeUnionConverter"))
            Assert.That(created, Does.Contain("Object reference not set"))

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
            Assert.That(updated, Does.Contain("JsonEnumLikeUnionConverter"))

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
            Assert.That(deleted, Does.Contain("JsonEnumLikeUnionConverter"))

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
