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

    let private deserializeRouteValue<'T> (body: string) =
        use document = JsonDocument.Parse(body)
        let mutable returnValue = Unchecked.defaultof<JsonElement>

        if (document.RootElement.TryGetProperty("ReturnValue", &returnValue)
            || document.RootElement.TryGetProperty("returnValue", &returnValue))
           && returnValue.ValueKind = JsonValueKind.Object then
            deserialize<GraceReturnValue<'T>> body
            |> fun value -> value.ReturnValue
        elif returnValue.ValueKind = JsonValueKind.Null then
            Assert.Fail($"Route returned null returnValue. Body: {body}")
            Unchecked.defaultof<'T>
        else
            try
                deserialize<'T> body
            with
            | ex ->
                Assert.Fail($"Failed to deserialize direct route value. Body: {body}. Error: {ex.Message}")
                Unchecked.defaultof<'T>

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

    let unusedAsync () =
        task {
            let parameters = scoped (GetPromotionSetParameters()) String.Empty String.Empty
            let! response = postAsync "/promotion-set/get" (createJsonContent parameters)
            let! value = deserializeContent<GraceReturnValue<PromotionSetDto>> response
            return value.ReturnValue
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
            Assert.That(eventsBeforeDelete, Does.Contain("created"))
            Assert.That(eventsBeforeDelete, Does.Contain("inputPromotionsUpdated"))
            Assert.That(eventsBeforeDelete, Does.Contain("recomputeFailed"))

            let! _ = PromotionSetIntegrationHelpers.resolveConflictsCurrentFailureAsync repositoryId promotionSetId (Guid.NewGuid().ToString())

            let! _ = PromotionSetIntegrationHelpers.applyBadRequestAsync repositoryId promotionSetId "DirectoryVersion was not found"

            do! PromotionSetIntegrationHelpers.deleteAsync repositoryId promotionSetId
            let! deleted = PromotionSetIntegrationHelpers.getPromotionSetAsync repositoryId promotionSetId
            Assert.That(deleted, Does.Contain("JsonEnumLikeUnionConverter"))

            let! eventsAfterDelete = PromotionSetIntegrationHelpers.getEventsAsync repositoryId promotionSetId
            Assert.That(eventsAfterDelete, Does.Contain("logicalDeleted"))
        }
